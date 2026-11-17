using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CloudSqlProxyManager
{
    public enum IconStyle
    {
        /// <summary>White cloud holding a navy database, on a dark navy tile.</summary>
        CloudNavy,
        /// <summary>White cloud holding a blue database, on a blue tile.</summary>
        Cloud,
        /// <summary>Cyan database on a dark navy tile.</summary>
        Navy,
        /// <summary>Blue database on a white tile.</summary>
        Light,
        /// <summary>White database on a flat blue tile (the first design).</summary>
        Classic
    }

    /// <summary>
    /// The one place the app icon is drawn. tools\make-icon.ps1 compiles this
    /// file to produce app.ico, and the running app uses it for the title bar,
    /// taskbar and notification area, so all of them are the same picture.
    /// A status badge can be added in the corner. Depends on System.Drawing only.
    /// </summary>
    public static class IconFactory
    {
        public const IconStyle DefaultStyle = IconStyle.CloudNavy;

        public static readonly Color Brand = Color.FromArgb(26, 115, 232);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(int size, Color? badge)
        {
            using (Bitmap bmp = Render(size, badge, DefaultStyle))
            {
                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        public static Bitmap Render(int size, Color? badge)
        {
            return Render(size, badge, DefaultStyle);
        }

        public static Bitmap Render(int size, Color? badge, IconStyle style)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float k = size / 32f; // everything below is laid out on a 32-unit grid

                switch (style)
                {
                    case IconStyle.Navy:
                        Tile(g, size, k, Color.FromArgb(40, 56, 80), Color.FromArgb(15, 23, 42), null);
                        Cylinder(g, k, 8f, 6f, 16f, 20f, Color.FromArgb(125, 211, 252), Color.FromArgb(59, 130, 246), Color.FromArgb(15, 23, 42));
                        break;

                    case IconStyle.Light:
                        Tile(g, size, k, Color.White, Color.FromArgb(240, 243, 247), Color.FromArgb(198, 204, 213));
                        Cylinder(g, k, 8f, 6f, 16f, 20f, Color.FromArgb(91, 155, 250), Color.FromArgb(25, 103, 210), Color.White);
                        break;

                    case IconStyle.Classic:
                        Tile(g, size, k, Brand, Brand, null);
                        Cylinder(g, k, 8f, 6f, 16f, 20f, Color.White, Color.White, Brand);
                        break;

                    case IconStyle.Cloud:
                        Tile(g, size, k, Color.FromArgb(84, 148, 248), Color.FromArgb(23, 99, 208), null);
                        CloudWithDatabase(g, k, Color.FromArgb(66, 133, 244), Color.FromArgb(23, 99, 208));
                        break;

                    default:
                        // A faint lighter edge keeps the dark tile visible on a dark taskbar.
                        Tile(g, size, k, Color.FromArgb(44, 62, 90), Color.FromArgb(15, 23, 42), Color.FromArgb(64, 82, 110));
                        CloudWithDatabase(g, k, Color.FromArgb(58, 80, 116), Color.FromArgb(15, 23, 42));
                        break;
                }

                if (badge.HasValue)
                {
                    // Status light, bottom right, with a white ring so it reads on any taskbar colour.
                    float d = 12.5f * k;
                    float ring = Math.Max(1f, 1.5f * k);
                    float bx = size - d - ring, by = size - d - ring;
                    using (SolidBrush white = new SolidBrush(Color.White))
                    using (SolidBrush fill = new SolidBrush(badge.Value))
                    {
                        g.FillEllipse(white, bx - ring, by - ring, d + 2 * ring, d + 2 * ring);
                        g.FillEllipse(fill, bx, by, d, d);
                    }
                }
            }
            return bmp;
        }

        /// <summary>A white cloud with a database inside it, laid out at 1x and enlarged about the centre.</summary>
        private static void CloudWithDatabase(Graphics g, float k, Color databaseTop, Color databaseBottom)
        {
            GraphicsState saved = g.Save();
            g.TranslateTransform(16f * k, 16.5f * k);
            g.ScaleTransform(1.16f, 1.16f);
            g.TranslateTransform(-16.5f * k, -16f * k);
            using (GraphicsPath cloud = new GraphicsPath(FillMode.Winding))
            using (SolidBrush white = new SolidBrush(Color.White))
            {
                cloud.AddEllipse(5.5f * k, 13.5f * k, 10f * k, 10f * k);
                cloud.AddEllipse(9.5f * k, 7.5f * k, 13f * k, 13f * k);
                cloud.AddEllipse(16.5f * k, 12f * k, 11f * k, 11f * k);
                using (GraphicsPath baseShape = RoundedRect(new RectangleF(5.5f * k, 17f * k, 22f * k, 7.5f * k), 3.75f * k))
                {
                    cloud.AddPath(baseShape, false);
                }
                g.FillPath(white, cloud);
            }
            Cylinder(g, k, 11.5f, 12f, 10f, 10.5f, databaseTop, databaseBottom, Color.White);
            g.Restore(saved);
        }

        private static void Tile(Graphics g, int size, float k, Color top, Color bottom, Color? border)
        {
            RectangleF bounds = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            using (GraphicsPath shape = RoundedRect(bounds, 7f * k))
            using (LinearGradientBrush fill = new LinearGradientBrush(new RectangleF(-1, -1, size + 2, size + 2), top, bottom, LinearGradientMode.Vertical))
            {
                g.FillPath(fill, shape);
                if (border.HasValue)
                {
                    using (Pen pen = new Pen(border.Value, Math.Max(1f, 0.5f * k)))
                    {
                        g.DrawPath(pen, shape);
                    }
                }
            }
        }

        /// <summary>A database: a cylinder with two bands. Coordinates are in 32-grid units.</summary>
        private static void Cylinder(Graphics g, float k, float x, float y, float w, float h, Color top, Color bottom, Color band)
        {
            float px = x * k, py = y * k, pw = w * k, ph = h * k;
            float eh = pw * 0.42f;
            using (LinearGradientBrush fill = new LinearGradientBrush(new RectangleF(px - 1, py - 1, pw + 2, ph + 2), top, bottom, LinearGradientMode.Vertical))
            using (Pen line = new Pen(band, Math.Max(1f, pw * 0.1f)))
            {
                g.FillRectangle(fill, px, py + eh / 2f, pw, ph - eh);
                g.FillEllipse(fill, px, py + ph - eh, pw, eh);
                g.FillEllipse(fill, px, py, pw, eh);
                float step = (ph - eh) / 3f;
                g.DrawArc(line, px, py + step, pw, eh, 0, 180);
                g.DrawArc(line, px, py + 2f * step, pw, eh, 0, 180);
            }
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Max(1f, radius * 2f);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
