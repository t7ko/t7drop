using System;
using System.Drawing;
using System.Windows.Forms;

namespace T7Drop
{
    /// <summary>
    /// What the windows share. They are built in code (there is no designer), sized in multiples
    /// of the font height (em), so that they grow with the display scale.
    /// </summary>
    static class Ui
    {
        public const string Title = "t7drop";

        public static int Setup(Form form, string title, bool dialog)
        {
            form.Text = title;
            form.Font = SystemFonts.MessageBoxFont;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = !dialog;
            form.MaximizeBox = !dialog;
            if (dialog)
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.ShowInTaskbar = true;
            try
            {
                form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // the default icon
            }
            return form.Font.Height;
        }

        public static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
        }

        /// <summary>A wrapped paragraph, at most `width` pixels wide.</summary>
        public static Label Paragraph(string text, int width)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(3, 3, 3, 6) };
        }

        public static Button Button(string text, int em, Action click)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(6 * em, 0) };
            button.Click += delegate { click(); };
            return button;
        }

        /// <summary>A label whose last words, linkText, are a link.</summary>
        public static LinkLabel Link(string text, string linkText, Action open)
        {
            var link = new LinkLabel { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
            link.LinkArea = new LinkArea(text.Length - linkText.Length, linkText.Length);
            link.LinkClicked += delegate { open(); };
            return link;
        }

        public static FlowLayoutPanel ButtonRow(int em, params Button[] buttons)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, em, 0, 0),
            };
            foreach (Button button in buttons)
                row.Controls.Add(button);
            return row;
        }

        public static TableLayoutPanel Table(int columns, int em)
        {
            return new TableLayoutPanel
            {
                ColumnCount = columns,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(em, em),
            };
        }

        public static void AddWide(TableLayoutPanel layout, Control control)
        {
            layout.Controls.Add(control, 0, layout.RowCount);
            layout.SetColumnSpan(control, layout.ColumnCount);
            layout.RowCount++;
        }

        public static void AddRow(TableLayoutPanel layout, params Control[] controls)
        {
            for (int column = 0; column < controls.Length; column++)
                layout.Controls.Add(controls[column], column, layout.RowCount);
            layout.RowCount++;
        }

        /// <summary>Sizes the form around its table.</summary>
        public static void Fit(Form form, TableLayoutPanel layout, int em)
        {
            form.Controls.Add(layout);
            Size preferred = layout.PreferredSize;
            form.ClientSize = new Size(preferred.Width + 2 * em, preferred.Height + 2 * em);
        }

        public static string Shorten(string text, int max)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";
        }

        public static string FirstLine(string text, int max)
        {
            string line = (text ?? "").Trim();
            int end = line.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0)
                line = line.Substring(0, end).TrimEnd() + " …";
            return Shorten(line, max);
        }
    }
}
