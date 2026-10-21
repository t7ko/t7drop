using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace T7Drop
{
    /// <summary>What the tray icon shows, as its color.</summary>
    public enum TrayState
    {
        Online,     // connected and idle: blue
        Busy,       // sending or receiving, or items wait: green
        Offline,    // the server cannot be reached: gray
        Attention,  // not paired, or items failed: orange
    }

    /// <summary>
    /// Draws the icon (a colored disc with a white arrow down) as .ico data. The tray uses it at
    /// run time, and scripts/build.ps1 calls WriteIco for the exe's icon, so this file must
    /// compile on its own.
    /// </summary>
    public static class Icons
    {
        public static Color FillColor(TrayState state)
        {
            switch (state)
            {
                case TrayState.Busy: return Color.FromArgb(0x2E, 0xA0, 0x43);
                case TrayState.Offline: return Color.FromArgb(0x8C, 0x8C, 0x8C);
                case TrayState.Attention: return Color.FromArgb(0xE8, 0x8B, 0x0C);
                default: return Color.FromArgb(0x1E, 0x78, 0xD7);
            }
        }

        public static Icon CreateIcon(TrayState state, int size)
        {
            using (var stream = new MemoryStream(IcoBytes(state, new[] { size })))
                return new Icon(stream, size, size);
        }

        public static void WriteIco(string path, TrayState state, int[] sizes)
        {
            File.WriteAllBytes(path, IcoBytes(state, sizes));
        }

        public static Bitmap Draw(TrayState state, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float inset = size / 32f;
                Color fill = FillColor(state);
                using (var brush = new SolidBrush(fill))
                    g.FillEllipse(brush, inset, inset, size - 2 * inset, size - 2 * inset);
                // The arrow: a shaft and a head, pointing down, with a bar under it (a tray).
                float s = size / 16f;
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillRectangle(white, 6.75f * s, 3f * s, 2.5f * s, 5.5f * s);
                    g.FillPolygon(white, new[]
                    {
                        new PointF(3.5f * s, 7.5f * s), new PointF(12.5f * s, 7.5f * s), new PointF(8f * s, 12f * s),
                    });
                    g.FillRectangle(white, 4f * s, 12.5f * s, 8f * s, 1.25f * s);
                }
            }
            return bitmap;
        }

        /// <summary>An .ico file with one image per size: 256 as PNG, smaller ones as 32-bit DIBs.</summary>
        public static byte[] IcoBytes(TrayState state, int[] sizes)
        {
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (Bitmap bitmap = Draw(state, size))
                    images.Add(size >= 256 ? Png(bitmap) : Dib(bitmap));

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((short)0);  // reserved
                writer.Write((short)1);  // type: icon
                writer.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte side = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);  // 0 means 256
                    writer.Write(side);
                    writer.Write(side);
                    writer.Write((byte)0);   // no palette
                    writer.Write((byte)0);   // reserved
                    writer.Write((short)1);  // color planes
                    writer.Write((short)32); // bits per pixel
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images)
                    writer.Write(image);
                writer.Flush();
                return stream.ToArray();
            }
        }

        static byte[] Png(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        static byte[] Dib(Bitmap bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height;
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            var pixels = new byte[stride * height];  // BGRA, top row first
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bitmap.UnlockBits(data);

            // The AND mask marks fully transparent pixels; it matters only where alpha is ignored.
            int maskStride = (width + 31) / 32 * 4;
            var mask = new byte[maskStride * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (pixels[y * stride + x * 4 + 3] == 0)
                        mask[(height - 1 - y) * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(40);           // BITMAPINFOHEADER size
                writer.Write(width);
                writer.Write(height * 2);   // the color image and the mask
                writer.Write((short)1);     // planes
                writer.Write((short)32);    // bits per pixel
                writer.Write(0);            // BI_RGB
                writer.Write(width * height * 4 + mask.Length);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                for (int y = height - 1; y >= 0; y--)  // DIB rows go bottom-up
                    writer.Write(pixels, y * stride, width * 4);
                writer.Write(mask);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
