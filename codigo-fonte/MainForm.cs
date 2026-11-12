using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FotosParaPdf
{
    internal static class ImageTools
    {
        internal static double ValidDpi(double dpi)
        {
            return Double.IsNaN(dpi) || Double.IsInfinity(dpi) || dpi < 1 ? 96 : dpi;
        }

        internal static int ApplyOrientation(Image image)
        {
            int orientation = 1;
            if (image.PropertyIdList.Contains(0x112))
            {
                byte[] value = image.GetPropertyItem(0x112).Value;
                if (value.Length >= 2) orientation = BitConverter.ToUInt16(value, 0);
            }
            RotateFlipType[] rotations = {
                RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone,
                RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone,
                RotateFlipType.Rotate180FlipX, RotateFlipType.Rotate90FlipX,
                RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX,
                RotateFlipType.Rotate270FlipNone
            };
            if (orientation >= 2 && orientation <= 8) image.RotateFlip(rotations[orientation]);
            return orientation;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly ListBox files = new ListBox();
        private readonly Label count = new Label();
        private readonly Label status = new Label();
        private readonly Button create = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly RadioButton combined = new RadioButton();
        private readonly RadioButton separate = new RadioButton();
        private readonly ComboBox pageSize = new ComboBox();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly List<Control> editControls = new List<Control>();
        private readonly SemaphoreSlim thumbnailSlots = new SemaphoreSlim(2);
        private readonly Font fileFont = new Font("Segoe UI", 11F, FontStyle.Bold);
        private readonly Font detailFont = new Font("Segoe UI", 9F);
        private bool busy;
        private bool closing;
        private int hoverIndex = -1;

        internal bool SeparateMode { get { return separate.Checked; } set { separate.Checked = value; combined.Checked = !value; } }
        internal int PendingThumbnails { get { return files.Items.Cast<Photo>().Count(p => !p.Completed); } }

        internal MainForm(IEnumerable<string> paths)
        {
            Text = "Imagens para PDF";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(246, 248, 251);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(790, 700);
            MinimumSize = new Size(700, 590);
            AutoScaleMode = AutoScaleMode.Dpi;
            AllowDrop = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float height in new[] { 40F, 40F, 38F, 40F, 30F }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (float height in new[] { 48F, 28F, 56F }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Suas imagens, do seu jeito", Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true }, 0, 0);
            layout.Controls.Add(new Label { Text = "Confira as prévias e escolha como salvar.\nTudo acontece neste computador; os originais são preservados.", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(76, 88, 106) }, 0, 1);

            var modePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            combined.Text = "Juntar em um PDF";
            combined.AutoSize = true;
            combined.Checked = true;
            combined.Margin = new Padding(0, 0, 28, 0);
            separate.Text = "Um PDF por imagem";
            separate.AutoSize = true;
            modePanel.Controls.Add(combined);
            modePanel.Controls.Add(separate);
            combined.CheckedChanged += delegate { UpdateCount(); files.Invalidate(); };
            separate.CheckedChanged += delegate { UpdateCount(); files.Invalidate(); };
            editControls.Add(combined);
            editControls.Add(separate);
            layout.Controls.Add(modePanel, 0, 2);

            var sizePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            sizePanel.Controls.Add(new Label { Text = "Tamanho da página:", AutoSize = true, Margin = new Padding(0, 5, 12, 0) });
            pageSize.DropDownStyle = ComboBoxStyle.DropDownList;
            pageSize.Width = 330;
            pageSize.Items.AddRange(new object[] { "Igual à imagem (sem margens)", "A4 (ajustar sem recortar)" });
            pageSize.SelectedIndex = 0;
            sizePanel.Controls.Add(pageSize);
            editControls.Add(pageSize);
            layout.Controls.Add(sizePanel, 0, 3);

            count.Dock = DockStyle.Fill;
            count.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(count, 0, 4);
            files.Dock = DockStyle.Fill;
            files.IntegralHeight = false;
            files.DisplayMember = "Name";
            files.SelectionMode = SelectionMode.One;
            files.BorderStyle = BorderStyle.FixedSingle;
            files.DrawMode = DrawMode.OwnerDrawFixed;
            files.ItemHeight = 100;
            files.DrawItem += DrawPhoto;
            files.SelectedIndexChanged += delegate { UpdateCount(); };
            files.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                int index = files.IndexFromPoint(e.Location);
                if (index != hoverIndex)
                {
                    hoverIndex = index;
                    toolTip.SetToolTip(files, index >= 0 ? ((Photo)files.Items[index]).Path : "");
                }
            };
            layout.Controls.Add(files, 0, 5);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            AddButton(toolbar, "Adicionar imagens", ChooseFiles, 150);
            AddButton(toolbar, "Subir", delegate { MovePhoto(-1); }, 72);
            AddButton(toolbar, "Descer", delegate { MovePhoto(1); }, 72);
            AddButton(toolbar, "Remover", RemovePhoto, 94);
            layout.Controls.Add(toolbar, 0, 6);
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(0, 7, 0, 7);
            layout.Controls.Add(progress, 0, 7);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            status.Text = "Escolha as imagens e o formato de saída.";
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Color.FromArgb(76, 88, 106);
            footer.Controls.Add(status, 0, 0);
            create.Dock = DockStyle.Fill;
            create.Margin = new Padding(8, 7, 0, 3);
            create.BackColor = Color.FromArgb(29, 78, 216);
            create.ForeColor = Color.White;
            create.FlatStyle = FlatStyle.Flat;
            create.FlatAppearance.BorderSize = 0;
            create.Click += async delegate { await CreatePdf(); };
            footer.Controls.Add(create, 1, 0);
            layout.Controls.Add(footer, 0, 8);
            DragEnter += delegate(object sender, DragEventArgs e) { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object sender, DragEventArgs e) { if (!busy) AddFiles((string[])e.Data.GetData(DataFormats.FileDrop)); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            Shown += delegate { StartThumbnails(); };
            AddFiles(paths);
        }

        private void DrawPhoto(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= files.Items.Count) return;
            var photo = (Photo)files.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var background = new SolidBrush(selected ? Color.FromArgb(229, 239, 255) : Color.White)) e.Graphics.FillRectangle(background, e.Bounds);
            var box = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 6, 88, 88);
            e.Graphics.FillRectangle(Brushes.White, box);
            if (photo.Thumbnail != null) e.Graphics.DrawImageUnscaled(photo.Thumbnail, box.Location);
            else TextRenderer.DrawText(e.Graphics, photo.Completed ? "Sem prévia" : "Carregando...", detailFont, box, Color.FromArgb(100, 116, 139), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            using (var border = new Pen(Color.FromArgb(219, 225, 233))) e.Graphics.DrawRectangle(border, box);
            var title = new Rectangle(box.Right + 16, e.Bounds.Y + 18, Math.Max(1, e.Bounds.Width - 132), 28);
            var detail = new Rectangle(title.X, e.Bounds.Y + 50, title.Width, 24);
            TextRenderer.DrawText(e.Graphics, (e.Index + 1) + ". " + photo.Name, fileFont, title, Color.FromArgb(24, 39, 63), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            string description = photo.Error != null ? "Não foi possível carregar a prévia" : photo.Completed ? photo.Width + " × " + photo.Height + " pixels  ·  " + (SeparateMode ? "PDF individual" : "Página " + (e.Index + 1)) : "Preparando a prévia";
            TextRenderer.DrawText(e.Graphics, description, detailFont, detail, Color.FromArgb(83, 98, 119), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            using (var line = new Pen(Color.FromArgb(235, 239, 244))) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
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
                MessageBox.Show(this, "Aguarde a conversão atual antes de adicionar novas imagens.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AddFiles(paths);
            var existing = files.Items.Cast<Photo>().ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
            var sorted = Program.Prepare(existing.Keys);
            files.BeginUpdate();
            files.Items.Clear();
            foreach (string path in sorted) files.Items.Add(existing[path]);
            files.EndUpdate();
            UpdateCount();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            var existing = new HashSet<string>(files.Items.Cast<Photo>().Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
            foreach (string path in Program.Prepare(paths)) if (existing.Add(path)) files.Items.Add(new Photo(path));
            UpdateCount();
            if (Visible) StartThumbnails();
        }

        private void StartThumbnails()
        {
            foreach (Photo photo in files.Items.Cast<Photo>().ToArray())
                if (!photo.Requested) { photo.Requested = true; LoadThumbnail(photo); }
        }

        private async void LoadThumbnail(Photo photo)
        {
            await thumbnailSlots.WaitAsync();
            try
            {
                if (closing || photo.Removed) return;
                Preview preview = await Task.Run(() => Photo.LoadPreview(photo.Path));
                if (closing || photo.Removed) { if (preview.Bitmap != null) preview.Bitmap.Dispose(); return; }
                photo.Thumbnail = preview.Bitmap;
                photo.Width = preview.Width;
                photo.Height = preview.Height;
                photo.Error = preview.Error;
                photo.Completed = true;
                files.Invalidate();
            }
            finally { thumbnailSlots.Release(); }
        }

        private void ChooseFiles()
        {
            using (var dialog = new OpenFileDialog { Filter = "Imagens JPG e PNG|*.jpg;*.jpeg;*.png", Multiselect = true, Title = "Escolha as imagens" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
        }

        private void RemovePhoto()
        {
            int index = files.SelectedIndex;
            if (index < 0) return;
            ((Photo)files.Items[index]).Dispose();
            files.Items.RemoveAt(index);
            if (files.Items.Count > 0) files.SelectedIndex = Math.Min(index, files.Items.Count - 1);
            UpdateCount();
        }

        private void MovePhoto(int direction)
        {
            int oldIndex = files.SelectedIndex, newIndex = oldIndex + direction;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= files.Items.Count) return;
            object item = files.Items[oldIndex];
            files.Items.RemoveAt(oldIndex);
            files.Items.Insert(newIndex, item);
            files.SelectedIndex = newIndex;
            files.Invalidate();
        }

        private void UpdateCount()
        {
            int total = files.Items.Count;
            string images = total + (total == 1 ? " imagem" : " imagens");
            count.Text = total == 0 ? "Adicione imagens ou arraste os arquivos para esta janela." : SeparateMode ? images + "  ·  " + total + (total == 1 ? " PDF individual" : " PDFs individuais") : images + "  ·  1 PDF com " + total + (total == 1 ? " página" : " páginas");
            create.Text = SeparateMode ? "Criar PDFs..." : "Criar PDF...";
            create.Enabled = !busy && total > 0;
        }

        private async Task CreatePdf()
        {
            string[] images = files.Items.Cast<Photo>().Select(p => p.Path).ToArray();
            if (images.Length == 0) return;
            bool individual = SeparateMode;
            PageSizing sizing = pageSize.SelectedIndex == 0 ? PageSizing.Image : PageSizing.A4;
            string destination;
            if (individual)
            {
                using (var dialog = new FolderBrowserDialog { Description = "Escolha a pasta para os PDFs individuais", SelectedPath = Path.GetDirectoryName(images[0]), ShowNewFolderButton = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    destination = dialog.SelectedPath;
                }
            }
            else
            {
                using (var dialog = new SaveFileDialog { Filter = "Documento PDF|*.pdf", DefaultExt = "pdf", AddExtension = true, OverwritePrompt = true, FileName = images.Length == 1 ? Path.GetFileNameWithoutExtension(images[0]) + ".pdf" : "Imagens.pdf", InitialDirectory = Path.GetDirectoryName(images[0]), Title = "Salvar as imagens em um único PDF" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    destination = dialog.FileName;
                }
            }
            busy = true;
            create.Enabled = false;
            foreach (Control control in editControls) control.Enabled = false;
            progress.Maximum = images.Length;
            progress.Value = 0;
            status.ForeColor = Color.FromArgb(76, 88, 106);
            status.Text = "Preparando " + images.Length + (images.Length == 1 ? " imagem..." : " imagens...");
            var report = new Progress<int>(n => { progress.Value = n; status.Text = "Convertendo " + n + " de " + images.Length + "..."; });
            try
            {
                if (individual)
                {
                    ConversionResult result = await Task.Run(() => Pdf.CreateSeparate(images, destination, n => ((IProgress<int>)report).Report(n), sizing));
                    status.Text = result.SavedFiles.Count + (result.SavedFiles.Count == 1 ? " PDF salvo" : " PDFs salvos") + (result.Errors.Count == 0 ? " na pasta escolhida." : "; " + result.Errors.Count + " imagem(ns) com erro.");
                    if (result.Errors.Count > 0) MessageBox.Show(this, status.Text + "\n\n" + String.Join("\n\n", result.Errors.Take(5)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    status.ForeColor = result.Errors.Count == 0 ? Color.FromArgb(21, 112, 66) : Color.FromArgb(164, 78, 0);
                }
                else
                {
                    await Task.Run(() => Pdf.Create(images, destination, n => ((IProgress<int>)report).Report(n), sizing));
                    status.Text = "PDF salvo: " + Path.GetFileName(destination);
                    status.ForeColor = Color.FromArgb(21, 112, 66);
                }
                toolTip.SetToolTip(status, destination);
            }
            catch (Exception ex)
            {
                status.Text = "Não foi possível concluir a conversão.";
                status.ForeColor = Color.FromArgb(176, 35, 35);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                foreach (Control control in editControls) control.Enabled = true;
                UpdateCount();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true;
                foreach (Photo photo in files.Items.Cast<Photo>()) photo.Dispose();
                toolTip.Dispose();
                fileFont.Dispose();
                detailFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class Preview
        {
            internal Bitmap Bitmap;
            internal int Width, Height;
            internal string Error;
        }

        private sealed class Photo : IDisposable
        {
            internal readonly string Path;
            internal Bitmap Thumbnail;
            internal int Width, Height;
            internal bool Requested, Completed, Removed;
            internal string Error;
            public string Name { get { return System.IO.Path.GetFileName(Path); } }
            internal Photo(string path) { Path = path; }
            public override string ToString() { return Name; }
            internal static Preview LoadPreview(string path)
            {
                Bitmap thumbnail = null;
                try
                {
                    using (Image image = Image.FromFile(path, true))
                    {
                        ImageTools.ApplyOrientation(image);
                        thumbnail = new Bitmap(88, 88, PixelFormat.Format24bppRgb);
                        using (Graphics graphics = Graphics.FromImage(thumbnail))
                        {
                            graphics.Clear(Color.White);
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            double scale = Math.Min(84.0 / image.Width, 84.0 / image.Height);
                            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                            int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                            graphics.DrawImage(image, new Rectangle((88 - width) / 2, (88 - height) / 2, width, height));
                        }
                        return new Preview { Bitmap = thumbnail, Width = image.Width, Height = image.Height };
                    }
                }
                catch (Exception ex)
                {
                    if (thumbnail != null) thumbnail.Dispose();
                    return new Preview { Error = ex.Message };
                }
            }
            public void Dispose() { Removed = true; if (Thumbnail != null) { Thumbnail.Dispose(); Thumbnail = null; } }
        }
    }
}
