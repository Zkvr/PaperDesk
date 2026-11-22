using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PaperDesk.Desktop.Controls;

internal sealed class TrayMenu : Forms.ContextMenuStrip
{
    internal TrayMenu()
    {
        ShowImageMargin = false;
        ShowCheckMargin = false;
        Padding = new Forms.Padding(5);
        ForeColor = Drawing.Color.FromArgb(242, 239, 232);
    }

    internal void ApplyTheme(DeskTheme theme)
    {
        if (SystemParameters.HighContrast)
        {
            ForeColor = Drawing.SystemColors.MenuText;
            BackColor = Drawing.SystemColors.Menu;
            Renderer = new Forms.ToolStripSystemRenderer();
            return;
        }
        ForeColor = Drawing.Color.FromArgb(242, 239, 232);
        BackColor = Convert(theme.Shell);
        Renderer = new Forms.ToolStripProfessionalRenderer(new Colors(theme)) { RoundedEdges = true };
    }

    private sealed class Colors(DeskTheme theme) : Forms.ProfessionalColorTable
    {
        private readonly Drawing.Color surface = Convert(theme.Shell);
        private readonly Drawing.Color border = Convert(theme.Panel);
        public override Drawing.Color ToolStripDropDownBackground => surface;
        public override Drawing.Color MenuBorder => border;
        public override Drawing.Color MenuItemSelected => border;
        public override Drawing.Color MenuItemBorder => Convert(theme.Accent);
        public override Drawing.Color SeparatorDark => border;
        public override Drawing.Color SeparatorLight => surface;
    }

    private static Drawing.Color Convert(Color color) => Drawing.Color.FromArgb(color.R, color.G, color.B);
}
