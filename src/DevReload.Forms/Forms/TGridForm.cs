using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DevReload.Forms
{
    /// <summary>
    /// Generic version of <see cref="StringGridForm"/> that works with any type <typeparamref name="T"/>.
    /// Displays a grid of buttons using a display-value function, and returns the
    /// selected <typeparamref name="T"/> instance.
    /// </summary>
    public partial class TGridForm<T> : Form
    {
        /// <summary>Gets the value selected by the user, or <c>default</c> if cancelled.</summary>
        public T? SelectedValue { get; private set; }

        private TableLayoutPanel panel;
        private OverlayForm overlay;
        private string overlayMessage;

        public TGridForm(IEnumerable<T> items, Func<T, string> displayValue, string message = "")
        {
            InitializeComponent();

            overlayMessage = message;

            if (items == null) throw new ArgumentNullException(nameof(items));
            if (displayValue == null) throw new ArgumentNullException(nameof(displayValue));

            var itemList = items.ToList();
            if (itemList.Count == 0) throw new ArgumentException("Items collection cannot be empty.", nameof(items));
            var displayTexts = itemList.Select(item => displayValue(item) ?? string.Empty).ToList();

            SelectedValue = default;
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;

            panel = new TableLayoutPanel()
            {
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                AutoSize = true,
            };

            int maxButtonHeight = 0;
            var font = new Font("Microsoft Sans Serif", 8.25F, FontStyle.Bold);

            int itemCount = displayTexts.Count;
            var sizes = displayTexts.Select(str => TextRenderer.MeasureText(str, font)).ToList();
            maxButtonHeight = sizes.Max(sz => sz.Height) + 20;

            int columns = (int)Math.Ceiling(Math.Sqrt(itemCount * 16.0 / 9.0));
            if (columns < 1) columns = 1;
            if (columns > itemCount) columns = itemCount;

            while (columns > 1)
            {
                int rowsCandidate = (int)Math.Ceiling((double)itemCount / columns);
                int[] tmpWidths = new int[columns];
                for (int i = 0; i < itemCount; i++)
                {
                    int col = i % columns;
                    tmpWidths[col] = Math.Max(tmpWidths[col], sizes[i].Width + 20);
                }
                int panelWidthCandidate = tmpWidths.Sum();
                int panelHeightCandidate = rowsCandidate * maxButtonHeight;
                if ((double)panelWidthCandidate / panelHeightCandidate <= 16.0 / 9.0)
                    break;
                columns--;
            }

            int rows = (int)Math.Ceiling((double)itemCount / columns);

            int[] columnWidths = new int[columns];
            for (int i = 0; i < itemCount; i++)
            {
                int col = i % columns;
                columnWidths[col] = Math.Max(columnWidths[col], sizes[i].Width + 20);
            }

            int idx = 0;
            foreach (var text in displayTexts)
            {
                Size textSize = TextRenderer.MeasureText(text, font);
                int col = idx % columns;
                columnWidths[col] = Math.Max(columnWidths[col], textSize.Width + 10);
                maxButtonHeight = Math.Max(maxButtonHeight, textSize.Height + 10);
                idx++;
            }

            if (columns > rows && columns * (rows - 1) >= itemCount)
                columns--;

            panel.ColumnCount = columns;
            panel.RowCount = rows;

            int buttonIndex = 0;
            foreach (var item in itemList)
            {
                string text = displayTexts[buttonIndex];
                int row = buttonIndex / columns;
                int col = buttonIndex % columns;

                Button btn = new Button
                {
                    Text = text,
                    AutoSize = false,
                    AutoEllipsis = true,
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(50, 50, 50),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    Font = font,
                    FlatStyle = FlatStyle.Flat,
                };
                btn.FlatAppearance.BorderColor = Color.FromArgb(40, 40, 40);
                btn.FlatAppearance.BorderSize = 1;
                var selectedItem = item;
                btn.Click += (sender, e) => { ButtonClicked(selectedItem); };
                btn.GotFocus += button_GotFocus;
                btn.LostFocus += button_LostFocus;
                btn.KeyDown += StringGridForm_KeyDown;
                btn.PreviewKeyDown += StringGridForm_PreviewKeyDown;

                if (col == 0)
                    panel.RowStyles.Add(new RowStyle(SizeType.Absolute, maxButtonHeight));
                panel.Controls.Add(btn, col, row);
                buttonIndex++;
            }

            for (int i = 0; i < columns; i++)
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, columnWidths[i]));

            int panelWidth = columnWidths.Sum();
            int panelHeight = maxButtonHeight * rows;
            this.ClientSize = new Size(panelWidth, panelHeight);
            this.Controls.Add(panel);
            this.AutoScroll = true;
            this.AutoSize = true;
        }

        private void ButtonClicked(T value)
        {
            SelectedValue = value;
            this.Close();
        }

        private void StringGridForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (overlay != null) overlay.Close();
        }

        private void StringGridForm_Load(object sender, EventArgs e)
        {
            StartPosition = FormStartPosition.Manual;
            Point cursorPosition = Cursor.Position;
            int formX = cursorPosition.X - this.Width / 2;
            int formY = cursorPosition.Y + this.Height - this.ClientSize.Height;
            Location = new Point(formX, formY);

            overlay = new OverlayForm(this, overlayMessage);
            int offset = 20;
            overlay.StartPosition = FormStartPosition.Manual;
            overlay.Location = new Point(this.Location.X, this.Location.Y - overlay.Height - offset);
            overlay.Show();
        }

        private void button_GotFocus(object sender, EventArgs e)
        {
            Button focusedButton = (Button)sender;
            focusedButton.BackColor = Color.LightSkyBlue;
            focusedButton.ForeColor = Color.Red;
        }

        private void button_LostFocus(object sender, EventArgs e)
        {
            Button focusedButton = (Button)sender;
            focusedButton.BackColor = Color.FromArgb(50, 50, 50);
            focusedButton.ForeColor = Color.FromArgb(200, 200, 200);
        }

        private void StringGridForm_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                    e.IsInputKey = true;
                    break;
            }
        }

        private void StringGridForm_KeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Button focusedButton = panel.Controls.OfType<Button>().Where(b => b.Focused).FirstOrDefault();
            if (focusedButton == null) return;

            int currentRow = panel.GetRow(focusedButton);
            int currentColumn = panel.GetColumn(focusedButton);

            switch (e.KeyCode)
            {
                case Keys.Up:
                    { Button btn = null; while (btn == null) { currentRow--; if (currentRow < 0) currentRow = panel.RowCount - 1; btn = panel.GetControlFromPosition(currentColumn, currentRow) as Button; } btn.Focus(); }
                    break;
                case Keys.Down:
                    { Button btn = null; while (btn == null) { currentRow++; if (currentRow > panel.RowCount - 1) currentRow = 0; btn = panel.GetControlFromPosition(currentColumn, currentRow) as Button; } btn.Focus(); }
                    break;
                case Keys.Left:
                    { Button btn = null; while (btn == null) { currentColumn--; if (currentColumn < 0) currentColumn = panel.ColumnCount - 1; btn = panel.GetControlFromPosition(currentColumn, currentRow) as Button; } btn.Focus(); }
                    break;
                case Keys.Right:
                    { Button btn = null; while (btn == null) { currentColumn++; if (currentColumn > panel.ColumnCount - 1) currentColumn = 0; btn = panel.GetControlFromPosition(currentColumn, currentRow) as Button; } btn.Focus(); }
                    break;
                case Keys.Escape:
                    this.Close();
                    break;
            }
        }

        private void StringGridForm_Shown(object sender, EventArgs e)
        {
            if (panel.Controls.Count > 0)
            {
                int row = panel.RowCount / 2;
                int col = panel.ColumnCount / 2;
                var middleButton = panel.GetControlFromPosition(col, row) as Button;
                middleButton?.Focus();
            }
        }
    }
}
