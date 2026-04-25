using System;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// Warning Fix Assistant Tool Window — dockable inside Visual Studio.
    /// Hosts the WinForms WarningTodoPanel inside a WPF WindowsFormsHost.
    /// </summary>
    [Guid("d7e8f9a0-1b2c-3d4e-5f6a-7b8c9d0e1f2a")]
    public class WarningTodoToolWindow : ToolWindowPane
    {
        public WarningTodoPanel Panel { get; private set; }

        public WarningTodoToolWindow() : base(null)
        {
            this.Caption = "⚠️ Warning Fix Assistant";

            Panel = new WarningTodoPanel();
            Panel.Dock = System.Windows.Forms.DockStyle.Fill;

            WindowsFormsHost host = new WindowsFormsHost();
            host.Child = Panel;

            this.Content = host;
        }
    }
}
