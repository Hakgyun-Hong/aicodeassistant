using System;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// AI Chat Tool Window — dockable inside Visual Studio.
    /// Hosts the WinForms ChatToolWindowPanel inside a WPF WindowsFormsHost.
    /// </summary>
    [Guid("a8f3b9c1-2d4e-5f6a-7b8c-9d0e1f2a3b4c")]
    public class ChatToolWindow : ToolWindowPane
    {
        private ChatToolWindowPanel _panel;

        public ChatToolWindow() : base(null)
        {
            this.Caption = "AI Code Assistant";
            
            _panel = new ChatToolWindowPanel();
            _panel.Dock = System.Windows.Forms.DockStyle.Fill;

            // Wrap the WinForms control in a WPF host to ensure VS 2022 compatibility
            WindowsFormsHost host = new WindowsFormsHost();
            host.Child = _panel;

            this.Content = host;
        }
    }
}
