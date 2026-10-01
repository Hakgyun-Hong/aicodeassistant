using System;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// SAM Fix Assistant Tool Window — dockable inside Visual Studio.
    /// Hosts the WinForms SamTodoPanel inside a WPF WindowsFormsHost.
    /// </summary>
    [Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
    public class SamTodoToolWindow : ToolWindowPane
    {
        public SamTodoPanel Panel { get; private set; }

        public SamTodoToolWindow() : base(null)
        {
            this.Caption = "📊 SAM Fix Assistant";

            Panel = new SamTodoPanel();
            Panel.Dock = System.Windows.Forms.DockStyle.Fill;

            var host = new WindowsFormsHost { Child = Panel };
            this.Content = host;
        }
    }
}
