using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Fotos para PDF")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: ComVisible(false)]

namespace FotosParaPdf
{
    internal static class Program
    {
        internal static readonly Guid ClassId = new Guid("6997466C-7858-4BA7-9342-9F58BC142B98");
        internal const string Verb = "FotosParaPdf.Unir";
        internal static readonly string[] Extensions = { ".jpg", ".jpeg", ".png" };
        internal static int ExitCode;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length > 0 && args[0] == "--install") { Installer.Install(); return 0; }
                if (args.Length > 0 && args[0] == "--uninstall") { Installer.Uninstall(); return 0; }
                if (args.Length > 0 && args[0] == "--convert")
                {
                    if (args.Length < 3) throw new ArgumentException("Informe o PDF de destino e as imagens.");
                    Pdf.Create(args.Skip(2).ToArray(), args[1], null);
                    return 0;
                }
                if (args.Length > 2 && args[0] == "--ipc-test-server")
                {
                    Instance.TestServer(args[1], Int32.Parse(args[2], CultureInfo.InvariantCulture));
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--shell")
                {
                    throw new InvalidOperationException("Atualize a integração executando Instalar.cmd da versão atual.");
                }
                if (args.Length > 1 && args[0] == "--snapshot")
                {
                    using (var form = new MainForm(args.Skip(2)))
                    {
                        form.Show();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                            bmp.Save(args[1], ImageFormat.Png);
                        }
                    }
                    return 0;
                }
                Instance.Run(args.Where(a => a != "--add").ToArray());
                return 0;
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                if (args.Length > 0 && (args[0] == "--convert" || args[0] == "--ipc-test-server"))
                {
                    Console.Error.WriteLine(ex.ToString());
                    if (args.Length > 1) File.WriteAllText(args[1] + ".error.txt", ex.ToString());
                }
                else MessageBox.Show(ex.Message, "Fotos para PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        internal static bool IsImage(string path)
        {
            return Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        internal static extern int StrCmpLogicalW(string a, string b);

        internal static List<string> Prepare(IEnumerable<string> files)
        {
            var list = files.Where(IsImage).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Sort((a, b) => { int c = StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)); return c == 0 ? StringComparer.OrdinalIgnoreCase.Compare(a, b) : c; });
            return list;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly ListBox files = new ListBox();
        private readonly Label count = new Label();
        private readonly Label status = new Label();
        private readonly Button create = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly List<Control> editControls = new List<Control>();
        private bool busy;

        internal MainForm(IEnumerable<string> paths)
        {
            Text = "Fotos para PDF";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(246, 248, 251);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(700, 520);
            MinimumSize = new Size(640, 500);
            AutoScaleMode = AutoScaleMode.Dpi;
            AllowDrop = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Suas fotos, em um único PDF", Font = new Font("Segoe UI", 19, FontStyle.Bold), AutoSize = true }, 0, 0);
            layout.Controls.Add(new Label { Text = "Uma imagem por página A4, sem recortar. Confira a ordem abaixo.\nTodo o processamento acontece neste computador.", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(76, 88, 106) }, 0, 1);
            count.Dock = DockStyle.Fill;
            count.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(count, 0, 2);
            files.Dock = DockStyle.Fill;
            files.IntegralHeight = false;
            files.HorizontalScrollbar = true;
            files.DisplayMember = "Name";
            files.SelectionMode = SelectionMode.One;
            files.BorderStyle = BorderStyle.FixedSingle;
            files.Font = new Font("Segoe UI", 11);
            files.SelectedIndexChanged += delegate { UpdateCount(); };
            layout.Controls.Add(files, 0, 3);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            AddButton(toolbar, "Adicionar fotos", delegate { ChooseFiles(); }, 130);
            AddButton(toolbar, "Subir", delegate { MovePhoto(-1); }, 72);
            AddButton(toolbar, "Descer", delegate { MovePhoto(1); }, 72);
            AddButton(toolbar, "Remover", delegate { if (files.SelectedIndex >= 0) files.Items.RemoveAt(files.SelectedIndex); UpdateCount(); }, 94);
            layout.Controls.Add(toolbar, 0, 4);
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(0, 7, 0, 7);
            layout.Controls.Add(progress, 0, 5);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            status.Text = "As imagens originais são preservadas.";
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Color.FromArgb(76, 88, 106);
            footer.Controls.Add(status, 0, 0);
            create.Text = "Criar PDF...";
            create.Dock = DockStyle.Fill;
            create.Margin = new Padding(8, 7, 0, 3);
            create.BackColor = Color.FromArgb(29, 78, 216);
            create.ForeColor = Color.White;
            create.FlatStyle = FlatStyle.Flat;
            create.FlatAppearance.BorderSize = 0;
            create.Click += async delegate { await CreatePdf(); };
            footer.Controls.Add(create, 1, 0);
            layout.Controls.Add(footer, 0, 6);
            DragEnter += delegate(object sender, DragEventArgs e) { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object sender, DragEventArgs e) { if (!busy) AddFiles((string[])e.Data.GetData(DataFormats.FileDrop)); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            AddFiles(paths);
        }

        private void AddButton(Control parent, string text, Action action, int width)
        {
            var button = new Button { Text = text, Width = width, Height = 32, BackColor = Color.White, FlatStyle = FlatStyle.System };
            button.Click += delegate { action(); };
            parent.Controls.Add(button);
            editControls.Add(button);
        }

        internal void ReceiveFiles(IEnumerable<string> paths)
        {
            if (busy)
            {
                MessageBox.Show(this, "Aguarde a conversão atual antes de adicionar novas imagens.", "Fotos para PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AddFiles(paths);
            // Explorer may start one short-lived process per selected file.
            // Sorting the complete list makes the order independent of startup timing.
            var sorted = Program.Prepare(files.Items.Cast<Photo>().Select(p => p.Path));
            files.BeginUpdate();
            files.Items.Clear();
            foreach (string path in sorted) files.Items.Add(new Photo(path));
            files.EndUpdate();
            UpdateCount();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            var existing = new HashSet<string>(files.Items.Cast<Photo>().Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in Program.Prepare(paths)) if (existing.Add(path)) files.Items.Add(new Photo(path));
            UpdateCount();
        }

        private void ChooseFiles()
        {
            using (var dialog = new OpenFileDialog { Filter = "Fotos JPG e PNG|*.jpg;*.jpeg;*.png", Multiselect = true, Title = "Escolha as fotos" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
        }

        private void MovePhoto(int direction)
        {
            int oldIndex = files.SelectedIndex;
            int newIndex = oldIndex + direction;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= files.Items.Count) return;
            object item = files.Items[oldIndex];
            files.Items.RemoveAt(oldIndex);
            files.Items.Insert(newIndex, item);
            files.SelectedIndex = newIndex;
        }

        private void UpdateCount()
        {
            count.Text = files.Items.Count + (files.Items.Count == 1 ? " imagem · 1 página" : " imagens · " + files.Items.Count + " páginas");
            create.Enabled = !busy && files.Items.Count > 0;
        }

        private async Task CreatePdf()
        {
            string[] images = files.Items.Cast<Photo>().Select(p => p.Path).ToArray();
            string destination;
            using (var dialog = new SaveFileDialog { Filter = "Documento PDF|*.pdf", DefaultExt = "pdf", AddExtension = true, OverwritePrompt = true, FileName = images.Length == 1 ? Path.GetFileNameWithoutExtension(images[0]) + ".pdf" : "Fotos.pdf", InitialDirectory = Path.GetDirectoryName(images[0]), Title = "Salvar as fotos em um único PDF" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                destination = dialog.FileName;
            }
            busy = true;
            create.Enabled = false;
            foreach (Control control in editControls) control.Enabled = false;
            progress.Maximum = images.Length;
            progress.Value = 0;
            var report = new Progress<int>(n => { progress.Value = n; status.Text = "Convertendo " + n + " de " + images.Length + "..."; });
            try
            {
                await Task.Run(() => Pdf.Create(images, destination, n => ((IProgress<int>)report).Report(n)));
                status.Text = "PDF criado com " + images.Length + (images.Length == 1 ? " página." : " páginas.");
                if (MessageBox.Show(this, "Seu PDF foi salvo.\n\nDeseja abrir o arquivo?", "PDF criado", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                status.Text = "Não foi possível criar o PDF.";
                MessageBox.Show(this, ex.Message, "Fotos para PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                foreach (Control control in editControls) control.Enabled = true;
                UpdateCount();
            }
        }

        private sealed class Photo
        {
            internal readonly string Path;
            public string Name { get { return System.IO.Path.GetFileName(Path); } }
            internal Photo(string path) { Path = path; }
            public override string ToString() { return Name; }
        }
    }

    internal sealed class Pdf : IDisposable
    {
        private readonly FileStream output;
        private readonly List<long> offsets = new List<long> { 0 };
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private Pdf(string path) { output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        private void Text(string text) { byte[] b = Encoding.ASCII.GetBytes(text); output.Write(b, 0, b.Length); }
        private void Object(int number, string text) { StartObject(number); Text(text + "\nendobj\n"); }
        private void StartObject(int number)
        {
            if (number != offsets.Count) throw new InvalidOperationException("Ordem de objetos PDF inválida.");
            offsets.Add(output.Position);
            Text(number + " 0 obj\n");
        }
        private void StreamObject(int number, string dictionary, byte[] data)
        {
            StartObject(number);
            Text("<< " + dictionary + " /Length " + data.Length + " >>\nstream\n");
            output.Write(data, 0, data.Length);
            Text("\nendstream\nendobj\n");
        }
        private static string Number(double n) { return n.ToString("0.####", Invariant); }

        internal static void Create(IList<string> files, string destination, Action<int> progress)
        {
            if (files.Count == 0) throw new ArgumentException("Selecione pelo menos uma imagem JPG ou PNG.");
            destination = Path.GetFullPath(destination);
            if (!Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("O arquivo de destino precisa ter a extensão .pdf.");
            foreach (string file in files)
            {
                if (!Program.IsImage(file)) throw new ArgumentException("Formato não aceito: " + Path.GetFileName(file));
                if (Path.GetFullPath(file).Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("O destino não pode ser uma imagem original.");
                if (!File.Exists(file)) throw new FileNotFoundException("Imagem não encontrada: " + Path.GetFileName(file), file);
            }
            string temp = Path.Combine(Path.GetDirectoryName(destination), ".fotos-para-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var pdf = new Pdf(temp))
                {
                    pdf.Text("%PDF-1.4\n%FotosParaPDF\n");
                    pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
                    pdf.Object(2, "<< /Type /Pages /Count " + files.Count + " /Kids [ " + String.Join(" ", Enumerable.Range(0, files.Count).Select(i => (3 + i * 3) + " 0 R")) + " ] >>");
                    for (int i = 0; i < files.Count; i++)
                    {
                        try { pdf.AddPage(files[i], 3 + i * 3); }
                        catch (Exception ex) { throw new IOException("Não foi possível ler a imagem \"" + Path.GetFileName(files[i]) + "\".\n" + ex.Message, ex); }
                        if (progress != null) progress(i + 1);
                    }
                    long xref = pdf.output.Position;
                    pdf.Text("xref\n0 " + pdf.offsets.Count + "\n0000000000 65535 f \n");
                    foreach (long offset in pdf.offsets.Skip(1)) pdf.Text(offset.ToString("0000000000", Invariant) + " 00000 n \n");
                    pdf.Text("trailer\n<< /Size " + pdf.offsets.Count + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                    pdf.output.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private void AddPage(string path, int number)
        {
            using (Image image = Image.FromFile(path, true))
            {
                bool isPng = image.RawFormat.Guid == ImageFormat.Png.Guid;
                int orientation = 1;
                if (image.PropertyIdList.Contains(0x112))
                {
                    byte[] value = image.GetPropertyItem(0x112).Value;
                    if (value.Length >= 2) orientation = BitConverter.ToUInt16(value, 0);
                }
                RotateFlipType[] rotations = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX, RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
                if (orientation >= 2 && orientation <= 8) image.RotateFlip(rotations[orientation]);
                int width = image.Width, height = image.Height;
                double pageWidth = width > height ? 841.8898 : 595.2756;
                double pageHeight = width > height ? 595.2756 : 841.8898;
                double scale = Math.Min((pageWidth - 24) / width, (pageHeight - 24) / height);
                double w = width * scale, h = height * scale;
                Object(number, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + Number(pageWidth) + " " + Number(pageHeight) + "] /Resources << /XObject << /Im0 " + (number + 2) + " 0 R >> >> /Contents " + (number + 1) + " 0 R >>");
                string commands = "q\n" + Number(w) + " 0 0 " + Number(h) + " " + Number((pageWidth - w) / 2) + " " + Number((pageHeight - h) / 2) + " cm\n/Im0 Do\nQ\n";
                StreamObject(number + 1, "", Encoding.ASCII.GetBytes(commands));
                byte[] bytes;
                string filter;
                if (!isPng && orientation == 1 && JpegComponents(path) == 3)
                {
                    bytes = File.ReadAllBytes(path);
                    filter = "/DCTDecode";
                }
                else
                {
                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.Clear(Color.White);
                            graphics.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, width, height, GraphicsUnit.Pixel);
                        }
                        if (isPng) { bytes = CompressRgb(bitmap); filter = "/FlateDecode"; }
                        else
                        {
                            using (var buffer = new MemoryStream())
                            using (var parameters = new EncoderParameters(1))
                            {
                                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                                bitmap.Save(buffer, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
                                bytes = buffer.ToArray();
                            }
                            filter = "/DCTDecode";
                        }
                    }
                }
                StreamObject(number + 2, "/Type /XObject /Subtype /Image /Width " + width + " /Height " + height + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter " + filter, bytes);
            }
        }

        private static int JpegComponents(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadByte() != 0xff || reader.ReadByte() != 0xd8) return 0;
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    if (reader.ReadByte() != 0xff) return 0;
                    int marker;
                    do { marker = reader.ReadByte(); } while (marker == 0xff);
                    if (marker == 0xd9 || marker == 0xda) return 0;
                    if (marker == 0x01 || (marker >= 0xd0 && marker <= 0xd8)) continue;
                    int length = reader.ReadByte() * 256 + reader.ReadByte();
                    if (length < 2) return 0;
                    if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
                    {
                        reader.ReadByte(); reader.ReadBytes(4);
                        return reader.ReadByte();
                    }
                    reader.BaseStream.Seek(length - 2, SeekOrigin.Current);
                }
                return 0;
            }
        }

        private static byte[] CompressRgb(Bitmap bitmap)
        {
            using (var buffer = new MemoryStream())
            {
                buffer.WriteByte(0x78); buffer.WriteByte(0x9c);
                uint a = 1, b = 0;
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    using (var compressor = new DeflateStream(buffer, CompressionLevel.Optimal, true))
                    {
                        byte[] row = new byte[checked(bitmap.Width * 3)];
                        for (int y = 0; y < bitmap.Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                            for (int x = 0; x < row.Length; x += 3) { byte r = row[x + 2]; row[x + 2] = row[x]; row[x] = r; }
                            foreach (byte value in row) { a = (a + value) % 65521; b = (b + a) % 65521; }
                            compressor.Write(row, 0, row.Length);
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
                uint checksum = (b << 16) | a;
                buffer.WriteByte((byte)(checksum >> 24)); buffer.WriteByte((byte)(checksum >> 16));
                buffer.WriteByte((byte)(checksum >> 8)); buffer.WriteByte((byte)checksum);
                return buffer.ToArray();
            }
        }

        public void Dispose() { output.Dispose(); }
    }

    internal static class Installer
    {
        private static string ClassPath { get { return @"Software\Classes\CLSID\" + Program.ClassId.ToString("B"); } }
        private static string VerbPath(string extension) { return @"Software\Classes\SystemFileAssociations\" + extension + @"\shell\" + Program.Verb; }
        [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr a, IntPtr b);

        internal static void Install()
        {
            string source = Application.ExecutablePath;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FotosParaPdf");
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, "FotosParaPdf.exe");
            if (!Path.GetFullPath(source).Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true);
            // Remove the old COM activation registration, including DelegateExecute below.
            Registry.CurrentUser.DeleteSubKeyTree(ClassPath, false);
            foreach (string extension in Program.Extensions)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(VerbPath(extension)))
                {
                    key.SetValue("", "Criar PDF com as imagens");
                    key.SetValue("Icon", "\"" + target + "\",0");
                    key.SetValue("MultiSelectModel", "Player");
                }
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(VerbPath(extension) + @"\command"))
                {
                    key.DeleteValue("DelegateExecute", false);
                    key.SetValue("", "\"" + target + "\" --add \"%1\"");
                }
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FotosParaPdf"))
            {
                key.SetValue("DisplayName", "Fotos para PDF");
                key.SetValue("DisplayVersion", "1.2.0");
                key.SetValue("DisplayIcon", "\"" + target + "\",0");
                key.SetValue("Publisher", "Fotos para PDF (aplicativo local)");
                key.SetValue("UninstallString", "\"" + target + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            MessageBox.Show("Instalado!\n\nSelecione fotos JPG/PNG, clique com o botão direito e escolha \"Criar PDF com as imagens\".\n\nNo Windows 11, abra \"Mostrar mais opções\" para encontrar o comando.", "Fotos para PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal static void Uninstall()
        {
            foreach (string extension in Program.Extensions) Registry.CurrentUser.DeleteSubKeyTree(VerbPath(extension), false);
            Registry.CurrentUser.DeleteSubKeyTree(ClassPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FotosParaPdf", false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FotosParaPdf");
            string target = Path.Combine(folder, "FotosParaPdf.exe");
            bool ownExecutable = Application.ExecutablePath.Equals(target, StringComparison.OrdinalIgnoreCase);
            if (!ownExecutable && File.Exists(target)) File.Delete(target);
            MessageBox.Show("A opção foi removida do botão direito.\n\n" + (ownExecutable ? "O executável permanece em:\n" + folder + "\nVocê pode excluir essa pasta depois de fechar esta janela." : "Os PDFs e as imagens foram preservados."), "Fotos para PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

}
