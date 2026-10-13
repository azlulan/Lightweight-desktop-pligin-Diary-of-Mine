using System;
using System.Drawing;
using System.Windows.Forms;

namespace DiaryOfMine.Desktop.Services;

internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public TrayService(Icon icon, Action showWindow, Action exitApplication)
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开日记本", null, (_, _) => showWindow());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exitApplication());
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "Diary of Mine",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
