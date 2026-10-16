using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace AIAssistant
{
    /// <summary>
    /// Captures a WPF window using PrintWindow (works for layered/DPI-aware windows)
    /// and overlays numbered callout bubbles on top of each UI element's BoundingRectangle.
    ///
    /// DPI handling:
    ///   UI Automation BoundingRectangle returns physical screen pixels on Windows 8.1+.
    ///   We capture the window at its native size, so we subtract the window origin
    ///   (windowRect.Left / Top) to get relative coordinates inside the captured image.
    ///
    /// For multi-monitor DPI scenarios we additionally apply the per-window DPI scale
    /// so that the callout boxes align correctly even at 125% / 150% magnification.
    /// </summary>
    public static class ScreenCaptureEngine
    {
        // ─── Public API ────────────────────────────────────────────────────────

        /// <summary>Capture a window to a PNG file and return the file path.</summary>
        public static string CaptureWindow(IntPtr hwnd, string tempDir, string tag)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                Directory.CreateDirectory(tempDir);
                string outPath = Path.Combine(tempDir, $"{tag}.png");

                if (NativeMethods.IsIconic(hwnd))
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

                var bounds = GetPhysicalWindowBounds(hwnd);
                if (bounds.Width < 10 || bounds.Height < 10) return null;

                Bitmap bmp = null;

                // 1. Primary: CopyFromScreen (captures true unclipped GPU/DPI rendered pixels)
                try
                {
                    NativeMethods.SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(80);

                    bounds = GetPhysicalWindowBounds(hwnd);
                    var screenBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(screenBmp))
                    {
                        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                    }

                    if (!IsMostlyEmpty(screenBmp))
                        bmp = screenBmp;
                    else
                        screenBmp.Dispose();
                }
                catch (Exception ex)
                {
                    Log($"CopyFromScreen failed, falling back to PrintWindow: {ex.Message}");
                }

                // 2. Fallback: PrintWindow (for background/virtual desktop scenarios)
                if (bmp == null)
                {
                    bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        NativeMethods.PrintWindow(hwnd, hdc, 0x00000002);
                        g.ReleaseHdc(hdc);
                    }
                }

                bmp.Save(outPath, ImageFormat.Png);
                bmp.Dispose();
                return outPath;
            }
            catch (Exception ex)
            {
                Log($"CaptureWindow failed: {ex.Message}");
                return null;
            }
        }

        public static Rectangle GetPhysicalWindowBounds(IntPtr hwnd)
        {
            try
            {
                int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.RECT));
                int hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, size);
                if (hr == 0 && (r.Right - r.Left) > 10 && (r.Bottom - r.Top) > 10)
                {
                    return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                }
            }
            catch { }

            NativeMethods.GetWindowRect(hwnd, out var wr);
            return new Rectangle(wr.Left, wr.Top, Math.Max(0, wr.Right - wr.Left), Math.Max(0, wr.Bottom - wr.Top));
        }

        private static bool IsMostlyEmpty(Bitmap bmp)
        {
            int emptyCount = 0;
            int total = 0;
            int stepX = Math.Max(1, bmp.Width / 6);
            int stepY = Math.Max(1, bmp.Height / 6);
            for (int x = stepX; x < bmp.Width; x += stepX)
            {
                for (int y = stepY; y < bmp.Height; y += stepY)
                {
                    total++;
                    Color c = bmp.GetPixel(x, y);
                    if (c.A == 0 || (c.R == 0 && c.G == 0 && c.B == 0))
                        emptyCount++;
                }
            }
            return total > 0 && (double)emptyCount / total > 0.95;
        }

        /// <summary>
        /// Overlay numbered callout bubbles and colored rectangles onto the raw screenshot.
        /// Each CalloutItem contains the BoundingRectangle in SCREEN coordinates;
        /// we convert to IMAGE coordinates by subtracting windowRect origin.
        /// </summary>
        public static string AnnotateScreenshot(
            string rawScreenshotPath,
            List<CalloutItem> callouts,
            System.Windows.Rect windowRect,
            string tempDir,
            string tag)
        {
            if (string.IsNullOrEmpty(rawScreenshotPath) || !File.Exists(rawScreenshotPath))
                return rawScreenshotPath;
            if (callouts == null || callouts.Count == 0)
                return rawScreenshotPath;

            try
            {
                string outPath = Path.Combine(tempDir, $"{tag}.png");

                using (var src = Bitmap.FromFile(rawScreenshotPath) as Bitmap)
                {
                    if (src == null) return rawScreenshotPath;

                    // Compute DPI scale — image pixel / logical pixel ratio
                    double dpiX, dpiY;
                    using (var g0 = Graphics.FromImage(src))
                    {
                        dpiX = src.Width  / Math.Max(1, windowRect.Width);
                        dpiY = src.Height / Math.Max(1, windowRect.Height);
                    }
                    // Clamp to reasonable range (handles 96dpi where scale ≈ 1.0)
                    if (dpiX < 0.5 || dpiX > 4.0) dpiX = 1.0;
                    if (dpiY < 0.5 || dpiY > 4.0) dpiY = 1.0;

                    using (var bmp = new Bitmap(src))
                    using (var g   = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode     = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                        foreach (var callout in callouts)
                        {
                            DrawCallout(g, callout, windowRect, dpiX, dpiY);
                        }

                        bmp.Save(outPath, ImageFormat.Png);
                    }
                }
                return outPath;
            }
            catch (Exception ex)
            {
                Log($"AnnotateScreenshot failed: {ex.Message}");
                return rawScreenshotPath;
            }
        }

        /// <summary>
        /// Returns an in-memory annotated Bitmap (no file I/O).
        /// Used by ManualEditorPanel for dynamic callout editing preview.
        /// If callouts is null or empty, returns a clean copy of the raw screenshot.
        /// </summary>
        public static Bitmap RenderAnnotatedBitmap(
            string rawScreenshotPath,
            List<CalloutItem> callouts,
            System.Windows.Rect windowRect,
            bool showCallouts)
        {
            if (string.IsNullOrEmpty(rawScreenshotPath) || !File.Exists(rawScreenshotPath))
                return null;

            try
            {
                using (var src = Image.FromFile(rawScreenshotPath) as Bitmap)
                {
                    if (src == null) return null;

                    var result = new Bitmap(src);

                    if (!showCallouts || callouts == null || callouts.Count == 0)
                        return result;

                    double dpiX = result.Width  / Math.Max(1, windowRect.Width);
                    double dpiY = result.Height / Math.Max(1, windowRect.Height);
                    if (dpiX < 0.5 || dpiX > 4.0) dpiX = 1.0;
                    if (dpiY < 0.5 || dpiY > 4.0) dpiY = 1.0;

                    using (var g = Graphics.FromImage(result))
                    {
                        g.SmoothingMode     = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                        foreach (var callout in callouts)
                            DrawCallout(g, callout, windowRect, dpiX, dpiY);
                    }

                    return result;
                }
            }
            catch { return null; }
        }

        // ─── Private: drawing ──────────────────────────────────────────────────

        private static readonly Color[] CalloutColors =
        {
            Color.FromArgb(220, 0,   0  ),  // red
            Color.FromArgb(0,   120, 200),  // blue
            Color.FromArgb(0,   160, 0  ),  // green
            Color.FromArgb(200, 100, 0  ),  // orange
            Color.FromArgb(120, 0,   180),  // purple
            Color.FromArgb(0,   160, 160),  // teal
            Color.FromArgb(180, 0,   100),  // magenta
            Color.FromArgb(100, 80,  0  ),  // brown
        };

        private static void DrawCallout(
            Graphics g, CalloutItem callout,
            System.Windows.Rect windowRect,
            double dpiX, double dpiY)
        {
            // Convert screen coords → image coords
            float imgX = (float)((callout.Rect.Left - windowRect.Left) * dpiX);
            float imgY = (float)((callout.Rect.Top  - windowRect.Top ) * dpiY);
            float imgW = (float)(callout.Rect.Width  * dpiX);
            float imgH = (float)(callout.Rect.Height * dpiY);

            if (imgW < 1 || imgH < 1) return;

            Color color = CalloutColors[(callout.Number - 1) % CalloutColors.Length];

            // 1. Semi-transparent fill + colored border
            using (var fillBrush = new SolidBrush(Color.FromArgb(30, color.R, color.G, color.B)))
                g.FillRectangle(fillBrush, imgX, imgY, imgW, imgH);

            using (var borderPen = new Pen(color, 2.5f))
            {
                borderPen.DashStyle = DashStyle.Solid;
                g.DrawRectangle(borderPen, imgX, imgY, imgW, imgH);
            }

            // 2. Callout number bubble (circle in top-left corner of element)
            const float BUBBLE_SIZE = 20f;
            float bx = imgX + 2;
            float by = imgY - BUBBLE_SIZE / 2 - 1;

            // Clamp bubble inside image
            if (by < 0) by = imgY + 1;

            var bubbleRect = new RectangleF(bx, by, BUBBLE_SIZE, BUBBLE_SIZE);

            using (var bubbleBrush = new SolidBrush(color))
                g.FillEllipse(bubbleBrush, bubbleRect);

            using (var borderPen = new Pen(Color.White, 1.5f))
                g.DrawEllipse(borderPen, bubbleRect);

            using (var font = new Font("Arial", 8.5f, FontStyle.Bold))
            using (var textBrush = new SolidBrush(Color.White))
            {
                var sf = new StringFormat
                {
                    Alignment     = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(callout.Number.ToString(), font, textBrush, bubbleRect, sf);
            }
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][ScreenCapture] {msg}");
    }
}
