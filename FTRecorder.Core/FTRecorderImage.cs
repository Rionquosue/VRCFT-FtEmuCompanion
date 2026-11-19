using System;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>One video frame: 8-bit RGB, rows top to bottom, no padding.</summary>
    internal sealed class FTRecorderFrame
    {
        internal readonly int Width;
        internal readonly int Height;
        internal readonly byte[] Rgb;

        internal FTRecorderFrame(int width, int height)
        {
            Width = width;
            Height = height;
            Rgb = new byte[width * height * 3];
        }
    }

    /// <summary>
    /// A rotated rectangle in a frame, in pixels: its centre, its size, and its rotation in radians (the angle of its
    /// x axis from the frame's, y down).
    /// </summary>
    internal struct FTRecorderRect
    {
        internal float CenterX, CenterY, Width, Height, Rotation;

        /// <summary>The frame position of a point given in the rectangle's own 0..1 coordinates.</summary>
        internal void ToFrame(float u, float v, out float x, out float y)
        {
            float du = (u - 0.5f) * Width, dv = (v - 0.5f) * Height;
            float cos = MathF.Cos(Rotation), sin = MathF.Sin(Rotation);
            x = CenterX + du * cos - dv * sin;
            y = CenterY + du * sin + dv * cos;
        }
    }

    /// <summary>
    /// Cuts a rotated rectangle out of a frame into a model's input, with bilinear sampling. Pixels outside the frame
    /// are black.
    /// </summary>
    internal static class FTRecorderImage
    {
        /// <summary>RGB, interleaved (NHWC), each byte as byte * scale + offset.</summary>
        internal static void CropRgb(FTRecorderFrame frame, FTRecorderRect rect, int outWidth, int outHeight, float[] destination, float scale, float offset)
        {
            Sample(frame, rect, outWidth, outHeight, (index, r, g, b) =>
            {
                destination[index * 3] = r * scale + offset;
                destination[index * 3 + 1] = g * scale + offset;
                destination[index * 3 + 2] = b * scale + offset;
            });
        }

        /// <summary>Grey (BT.601 luma) / 255, planar (NCHW) with the same plane repeated for every channel.</summary>
        internal static void CropGray(FTRecorderFrame frame, FTRecorderRect rect, int outWidth, int outHeight, float[] destination, int channels)
        {
            int plane = outWidth * outHeight;
            Sample(frame, rect, outWidth, outHeight, (index, r, g, b) =>
            {
                float gray = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
                for (int c = 0; c < channels; c++) destination[c * plane + index] = gray;
            });
        }

        private delegate void Pixel(int index, float r, float g, float b);

        private static void Sample(FTRecorderFrame frame, FTRecorderRect rect, int outWidth, int outHeight, Pixel write)
        {
            float cos = MathF.Cos(rect.Rotation), sin = MathF.Sin(rect.Rotation);
            byte[] rgb = frame.Rgb;
            int width = frame.Width, height = frame.Height;
            for (int v = 0; v < outHeight; v++)
            {
                float dv = ((v + 0.5f) / outHeight - 0.5f) * rect.Height;
                for (int u = 0; u < outWidth; u++)
                {
                    float du = ((u + 0.5f) / outWidth - 0.5f) * rect.Width;
                    float x = rect.CenterX + du * cos - dv * sin - 0.5f;
                    float y = rect.CenterY + du * sin + dv * cos - 0.5f;
                    int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
                    float fx = x - x0, fy = y - y0;
                    float r = 0, g = 0, b = 0;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        int px = x0 + (corner & 1), py = y0 + (corner >> 1);
                        if (px < 0 || py < 0 || px >= width || py >= height) continue;
                        float weight = ((corner & 1) == 1 ? fx : 1 - fx) * ((corner >> 1) == 1 ? fy : 1 - fy);
                        int at = (py * width + px) * 3;
                        r += rgb[at] * weight;
                        g += rgb[at + 1] * weight;
                        b += rgb[at + 2] * weight;
                    }
                    write(v * outWidth + u, r, g, b);
                }
            }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder images
// ============================================================================
//
// FTRecorderFrame (this file)  Width, Height, Rgb (8-bit RGB, packed rows).
// FTRecorderRect             CenterX, CenterY, Width, Height (pixels), Rotation (radians, x axis from the frame's, y down).
//   ToFrame(u, v, out x, out y)  A point in the rectangle's 0..1 coordinates to frame pixels.
// FTRecorderImage
//   CropRgb(frame, rect, w, h, destination, scale, offset)  NHWC RGB, byte * scale + offset; bilinear, black outside.
//   CropGray(frame, rect, w, h, destination, channels)  NCHW grey / 255, the plane repeated per channel.
