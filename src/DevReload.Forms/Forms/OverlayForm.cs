using System;
using System.Drawing;
using System.Windows.Forms;

namespace DevReload.Forms
{
    /// <summary>
    /// A borderless, topmost overlay that displays a message above its parent form.
    /// Used by <see cref="StringGridForm"/> and <see cref="TGridForm{T}"/> to show
    /// a prompt label above the selection grid.
    /// </summary>
    public class OverlayForm : Form
    {
        private Form parentForm;

        public OverlayForm(Form parentForm, string message)
        {
            InitializeComponent();
            this.parentForm = parentForm;

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.BackColor = Color.Black;

            var font = new Font("Microsoft Sans Serif", 8.25F, FontStyle.Bold);
            Size textSize = TextRenderer.MeasureText(message, font);

            Label label = new Label
            {
                Text = message,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Font = font,
                Size = new Size(textSize.Width, textSize.Height),
            };

            this.Controls.Add(label);
            this.TopMost = true;
            this.ClientSize = new Size(label.Width, label.Height);
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.ClientSize = new Size(211, 27);
            this.Name = "OverlayForm";
            this.Shown += new EventHandler(this.OverlayForm_Shown);
            this.ResumeLayout(false);
        }

        private void OverlayForm_Shown(object sender, EventArgs e)
        {
            this.Location = new Point(
                parentForm.Location.X + (parentForm.Width - this.Width) / 2,
                parentForm.Location.Y - this.Height - 10);
        }
    }
}
