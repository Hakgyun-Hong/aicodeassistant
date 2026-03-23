using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// AI Chat Tool Window — dockable inside Visual Studio.
    /// Hosts the WinForms ChatToolWindowPanel.
    /// </summary>
    [Guid("a8f3b9c1-2d4e-5f6a-7b8c-9d0e1f2a3b4c")]
    public class ChatToolWindow : ToolWindowPane
    {
        private ChatToolWindowPanel _panel;

        public ChatToolWindow() : base(null)
        {
            this.Caption = "AI Code Assistant";
            _panel = new ChatToolWindowPanel();
            _panel.Dock = DockStyle.Fill;
        }

        /// <summary>
        /// Returns the WinForms control to host in the tool window.
        /// </summary>
        public override System.Windows.Forms.IWin32Window Window
        {
            get { return _panel; }
        }
    }
}
