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

[assembly: System.Reflection.AssemblyTitle("Imagens para PDF")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
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
                    Pdf.Create(args.Skip(2).Where(a => a != "--a4").ToArray(), args[1], null, args.Contains("--a4") ? PageSizing.A4 : PageSizing.Image);
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--convert-separate")
                {
                    if (args.Length < 3) throw new ArgumentException("Informe a pasta de destino e as imagens.");
                    var result = Pdf.CreateSeparate(args.Skip(2).Where(a => a != "--a4").ToArray(), args[1], null, args.Contains("--a4") ? PageSizing.A4 : PageSizing.Image);
                    if (result.Errors.Count > 0) throw new IOException(String.Join(Environment.NewLine, result.Errors));
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
                if (args.Length > 1 && (args[0] == "--snapshot" || args[0] == "--snapshot-separate"))
                {
                    using (var form = new MainForm(args.Skip(2)))
                    {
                        form.SeparateMode = args[0] == "--snapshot-separate";
                        form.Show();
                        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                        do { Application.DoEvents(); Thread.Sleep(20); } while (form.PendingThumbnails > 0 && DateTime.UtcNow < deadline);
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
                if (args.Length > 0 && (args[0] == "--convert" || args[0] == "--convert-separate" || args[0] == "--ipc-test-server"))
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

    internal enum PageSizing { Image, A4 }

    internal sealed class ConversionResult
    {
        internal readonly List<string> SavedFiles = new List<string>();
        internal readonly List<string> Errors = new List<string>();
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

        internal static ConversionResult CreateSeparate(IList<string> files, string folder, Action<int> progress, PageSizing sizing = PageSizing.Image)
        {
            if (files.Count == 0) throw new ArgumentException("Selecione pelo menos uma imagem.");
            folder = Path.GetFullPath(folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("A pasta de destino não foi encontrada.");
            var result = new ConversionResult();
            for (int i = 0; i < files.Count; i++)
            {
                try
                {
                    string stem = Path.GetFileNameWithoutExtension(files[i]);
                    int suffix = 1;
                    while (true)
                    {
                        string name = stem + (suffix == 1 ? "" : " (" + suffix + ")") + ".pdf";
                        string destination = Path.Combine(folder, name);
                        suffix++;
                        if (File.Exists(destination) || Directory.Exists(destination)) continue;
                        try
                        {
                            Create(new[] { files[i] }, destination, null, sizing, false);
                            result.SavedFiles.Add(destination);
                            break;
                        }
                        catch (IOException)
                        {
                            // If another process claimed this name, choose the next one without overwriting it.
                            if (!File.Exists(destination) && !Directory.Exists(destination)) throw;
                            if (suffix > 10000) throw;
                        }
                    }
                }
                catch (Exception ex) { result.Errors.Add(Path.GetFileName(files[i]) + ": " + ex.Message); }
                if (progress != null) progress(i + 1);
            }
            return result;
        }

        internal static void Create(IList<string> files, string destination, Action<int> progress, PageSizing sizing = PageSizing.Image, bool overwrite = true)
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
                        try { pdf.AddPage(files[i], 3 + i * 3, sizing); }
                        catch (Exception ex) { throw new IOException("Não foi possível ler a imagem \"" + Path.GetFileName(files[i]) + "\".\n" + ex.Message, ex); }
                        if (progress != null) progress(i + 1);
                    }
                    long xref = pdf.output.Position;
                    pdf.Text("xref\n0 " + pdf.offsets.Count + "\n0000000000 65535 f \n");
                    foreach (long offset in pdf.offsets.Skip(1)) pdf.Text(offset.ToString("0000000000", Invariant) + " 00000 n \n");
                    pdf.Text("trailer\n<< /Size " + pdf.offsets.Count + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                    pdf.output.Flush(true);
                }
                if (overwrite && File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private void AddPage(string path, int number, PageSizing sizing)
        {
            using (Image image = Image.FromFile(path, true))
            {
                bool isPng = image.RawFormat.Guid == ImageFormat.Png.Guid;
                double dpiX = ImageTools.ValidDpi(image.HorizontalResolution);
                double dpiY = ImageTools.ValidDpi(image.VerticalResolution);
                int orientation = ImageTools.ApplyOrientation(image);
                if (orientation >= 5 && orientation <= 8) { double swap = dpiX; dpiX = dpiY; dpiY = swap; }
                int width = image.Width, height = image.Height;
                double pageWidth, pageHeight, w, h;
                if (sizing == PageSizing.Image)
                {
                    pageWidth = w = width * 72.0 / dpiX;
                    pageHeight = h = height * 72.0 / dpiY;
                }
                else
                {
                    pageWidth = width > height ? 841.8898 : 595.2756;
                    pageHeight = width > height ? 595.2756 : 841.8898;
                    double scale = Math.Min((pageWidth - 24) / width, (pageHeight - 24) / height);
                    w = width * scale;
                    h = height * scale;
                }
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
                key.SetValue("DisplayName", "Imagens para PDF");
                key.SetValue("DisplayVersion", "1.3.0");
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
