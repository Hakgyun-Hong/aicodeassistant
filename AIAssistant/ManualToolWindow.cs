using System;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// WPF Auto Manual Generator — VS Tool Window.
    /// Hosts ManualEditorPanel via WinForms WindowsFormsHost.
    /// </summary>
    [Guid("b1c2d3e4-f5a6-7890-bcde-f01234567890")]
    public sealed class ManualToolWindow : ToolWindowPane
    {
        public ManualEditorPanel Panel { get; private set; }

        public ManualToolWindow() : base(null)
        {
            Caption = "📖 WPF Auto Manual Generator";
            Panel   = new ManualEditorPanel { Dock = System.Windows.Forms.DockStyle.Fill };
            Content = new WindowsFormsHost { Child = Panel };
        }
    }
}
