using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace T7Drop
{
    /// <summary>Error correction levels, in increasing strength.</summary>
    public enum Ecc
    {
        Low,
        Medium,
        Quartile,
        High,
    }

    /// <summary>
    /// A QR Code (Model 2) symbol in byte mode: the algorithm of Project Nayuki's QR Code
    /// generator library (MIT License, https://www.nayuki.io/page/qr-code-generator-library),
    /// ported to C# 5 and cut down to what the pairing invitation needs. tests/Tests.cs compares
    /// its symbols with segno's (tests/qr-vectors.txt).
    /// </summary>
    public sealed class QrCode
    {
        public readonly int Version, Size, Mask;
        public readonly Ecc ErrorCorrection;
        readonly bool[,] modules;    // [y, x], true for dark
        bool[,] isFunction;          // only while drawing

        /// <summary>The smallest symbol for data at ecl or better (boosted while the version stays); mask -1 picks the best.</summary>
        public static QrCode EncodeBytes(byte[] data, Ecc ecl, int mask = -1, bool boostEcl = true)
        {
            int version, usedBits;
            for (version = 1; ; version++)
            {
                usedBits = TotalBits(data.Length, version);
                if (usedBits >= 0 && usedBits <= NumDataCodewords(version, ecl) * 8)
                    break;
                if (version == 40)
                    throw new ArgumentException("Too much data for a QR code.");
            }
            if (boostEcl)
                foreach (Ecc stronger in new[] { Ecc.Medium, Ecc.Quartile, Ecc.High })
                    if (stronger > ecl && usedBits <= NumDataCodewords(version, stronger) * 8)
                        ecl = stronger;
            return EncodeBytes(data, ecl, version, mask);
        }

        /// <summary>A symbol of this version and level exactly (for tests).</summary>
        public static QrCode EncodeBytes(byte[] data, Ecc ecl, int version, int mask)
        {
            int usedBits = TotalBits(data.Length, version);
            int capacity = NumDataCodewords(version, ecl) * 8;
            if (usedBits < 0 || usedBits > capacity)
                throw new ArgumentException("The data does not fit this version.");
            var bits = new List<bool>();
            AppendBits(bits, 4, 4);  // byte mode
            AppendBits(bits, data.Length, version < 10 ? 8 : 16);
            foreach (byte b in data)
                AppendBits(bits, b, 8);
            AppendBits(bits, 0, Math.Min(4, capacity - bits.Count));  // terminator
            AppendBits(bits, 0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
                AppendBits(bits, pad, 8);
            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i])
                    codewords[i >> 3] |= (byte)(0x80 >> (i & 7));
            return new QrCode(version, ecl, codewords, mask);
        }

        static int TotalBits(int length, int version)
        {
            int countBits = version < 10 ? 8 : 16;
            return length >= 1 << countBits ? -1 : 4 + countBits + length * 8;
        }

        static void AppendBits(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        QrCode(int version, Ecc ecl, byte[] dataCodewords, int mask)
        {
            Version = version;
            ErrorCorrection = ecl;
            Size = version * 4 + 17;
            modules = new bool[Size, Size];
            isFunction = new bool[Size, Size];
            DrawFunctionPatterns();
            DrawCodewords(AddEccAndInterleave(dataCodewords));
            if (mask == -1)
            {
                int minPenalty = int.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    ApplyMask(i);
                    DrawFormatBits(i);
                    int penalty = PenaltyScore();
                    if (penalty < minPenalty)
                    {
                        mask = i;
                        minPenalty = penalty;
                    }
                    ApplyMask(i);  // undoes it
                }
            }
            Mask = mask;
            ApplyMask(mask);
            DrawFormatBits(mask);
            isFunction = null;
        }

        public bool IsDark(int x, int y)
        {
            return 0 <= x && x < Size && 0 <= y && y < Size && modules[y, x];
        }

        /// <summary>Black on white, scale pixels per module, with a light border of `border` modules.</summary>
        public Bitmap ToBitmap(int scale, int border)
        {
            int side = (Size + 2 * border) * scale;
            var bitmap = new Bitmap(side, side, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (modules[y, x])
                            g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
            }
            return bitmap;
        }

        /// <summary>The rows as "0" and "1" lines, for tests.</summary>
        public string Rows()
        {
            var text = new System.Text.StringBuilder();
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                    text.Append(modules[y, x] ? '1' : '0');
                text.Append('\n');
            }
            return text.ToString();
        }

        void DrawFunctionPatterns()
        {
            for (int i = 0; i < Size; i++)
            {
                SetFunctionModule(6, i, i % 2 == 0);
                SetFunctionModule(i, 6, i % 2 == 0);
            }
            DrawFinderPattern(3, 3);
            DrawFinderPattern(Size - 4, 3);
            DrawFinderPattern(3, Size - 4);
            int[] positions = AlignmentPatternPositions();
            int count = positions.Length;
            for (int i = 0; i < count; i++)
                for (int j = 0; j < count; j++)
                    if (!(i == 0 && j == 0 || i == 0 && j == count - 1 || i == count - 1 && j == 0))
                        DrawAlignmentPattern(positions[i], positions[j]);
            DrawFormatBits(0);  // a placeholder, so that the codewords skip it
            DrawVersion();
        }

        void DrawFormatBits(int mask)
        {
            int levelBits = ErrorCorrection == Ecc.Low ? 1 : ErrorCorrection == Ecc.Medium ? 0 : ErrorCorrection == Ecc.Quartile ? 3 : 2;
            int data = levelBits << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            for (int i = 0; i <= 5; i++)
                SetFunctionModule(8, i, Bit(bits, i));
            SetFunctionModule(8, 7, Bit(bits, 6));
            SetFunctionModule(8, 8, Bit(bits, 7));
            SetFunctionModule(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++)
                SetFunctionModule(14 - i, 8, Bit(bits, i));
            for (int i = 0; i < 8; i++)
                SetFunctionModule(Size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++)
                SetFunctionModule(8, Size - 15 + i, Bit(bits, i));
            SetFunctionModule(8, Size - 8, true);  // always dark
        }

        void DrawVersion()
        {
            if (Version < 7)
                return;
            int rem = Version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = Version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = Bit(bits, i);
                int a = Size - 11 + i % 3, b = i / 3;
                SetFunctionModule(a, b, bit);
                SetFunctionModule(b, a, bit);
            }
        }

        void DrawFinderPattern(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = x + dx, yy = y + dy;
                    if (0 <= xx && xx < Size && 0 <= yy && yy < Size)
                        SetFunctionModule(xx, yy, distance != 2 && distance != 4);
                }
        }

        void DrawAlignmentPattern(int x, int y)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunctionModule(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        void SetFunctionModule(int x, int y, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        byte[] AddEccAndInterleave(byte[] data)
        {
            int numBlocks = NumErrorCorrectionBlocks[(int)ErrorCorrection, Version];
            int blockEccLength = EccCodewordsPerBlock[(int)ErrorCorrection, Version];
            int rawCodewords = NumRawDataModules(Version) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLength = rawCodewords / numBlocks;
            byte[] divisor = ReedSolomonDivisor(blockEccLength);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int dataLength = shortBlockLength - blockEccLength + (i < numShortBlocks ? 0 : 1);
                var blockData = new byte[dataLength];
                Array.Copy(data, k, blockData, 0, dataLength);
                k += dataLength;
                var block = new byte[shortBlockLength + 1];
                Array.Copy(blockData, block, dataLength);
                byte[] ecc = ReedSolomonRemainder(blockData, divisor);
                Array.Copy(ecc, 0, block, block.Length - blockEccLength, ecc.Length);
                blocks.Add(block);
            }
            var result = new byte[rawCodewords];
            for (int i = 0, k = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortBlockLength - blockEccLength || j >= numShortBlocks)  // short blocks have a gap there
                        result[k++] = blocks[j][i];
            return result;
        }

        void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                    right = 5;  // the vertical timing pattern
                for (int vertical = 0; vertical < Size; vertical++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vertical : vertical;
                        if (!isFunction[y, x] && i < data.Length * 8)
                        {
                            modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
            }
        }

        void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        case 7: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                        default: throw new ArgumentException("No such mask: " + mask);
                    }
                    modules[y, x] ^= invert & !isFunction[y, x];
                }
        }

        const int PenaltyN1 = 3, PenaltyN2 = 3, PenaltyN3 = 40, PenaltyN4 = 10;

        int PenaltyScore()
        {
            int result = 0;
            for (int pass = 0; pass < 2; pass++)  // rows, then columns
                for (int a = 0; a < Size; a++)
                {
                    bool runColor = false;
                    int run = 0;
                    var history = new int[7];
                    for (int b = 0; b < Size; b++)
                    {
                        bool dark = pass == 0 ? modules[a, b] : modules[b, a];
                        if (dark == runColor)
                        {
                            run++;
                            if (run == 5)
                                result += PenaltyN1;
                            else if (run > 5)
                                result++;
                        }
                        else
                        {
                            AddHistory(run, history);
                            if (!runColor)
                                result += CountFinderPatterns(history) * PenaltyN3;
                            runColor = dark;
                            run = 1;
                        }
                    }
                    result += TerminateAndCount(runColor, run, history) * PenaltyN3;
                }
            for (int y = 0; y < Size - 1; y++)
                for (int x = 0; x < Size - 1; x++)
                {
                    bool color = modules[y, x];
                    if (color == modules[y, x + 1] && color == modules[y + 1, x] && color == modules[y + 1, x + 1])
                        result += PenaltyN2;
                }
            int dark2 = 0;
            foreach (bool module in modules)
                if (module)
                    dark2++;
            int total = Size * Size;
            int k = (Math.Abs(dark2 * 20 - total * 10) + total - 1) / total - 1;
            result += k * PenaltyN4;
            return result;
        }

        int CountFinderPatterns(int[] history)
        {
            int n = history[1];
            bool core = n > 0 && history[2] == n && history[3] == n * 3 && history[4] == n && history[5] == n;
            return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0)
                + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
        }

        int TerminateAndCount(bool runColor, int run, int[] history)
        {
            if (runColor)
            {
                AddHistory(run, history);
                run = 0;
            }
            run += Size;  // the light border after the last run
            AddHistory(run, history);
            return CountFinderPatterns(history);
        }

        void AddHistory(int run, int[] history)
        {
            if (history[0] == 0)
                run += Size;  // the light border before the first run
            Array.Copy(history, 0, history, 1, history.Length - 1);
            history[0] = run;
        }

        int[] AlignmentPatternPositions()
        {
            if (Version == 1)
                return new int[0];
            int count = Version / 7 + 2;
            int step = (Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var result = new int[count];
            result[0] = 6;
            for (int i = count - 1, position = Size - 7; i >= 1; i--, position -= step)
                result[i] = position;
            return result;
        }

        static int NumRawDataModules(int version)
        {
            int size = version * 4 + 17;
            int result = size * size;
            result -= 8 * 8 * 3;          // finder patterns with separators
            result -= 15 * 2 + 1;         // format bits and the dark module
            result -= (size - 16) * 2;    // timing patterns
            if (version >= 2)
            {
                int count = version / 7 + 2;
                result -= (count - 1) * (count - 1) * 25;  // alignment patterns
                result -= (count - 2) * 2 * 20;            // where they cross the timing patterns
                if (version >= 7)
                    result -= 6 * 3 * 2;  // version information
            }
            return result;
        }

        static int NumDataCodewords(int version, Ecc ecl)
        {
            return NumRawDataModules(version) / 8
                - EccCodewordsPerBlock[(int)ecl, version] * NumErrorCorrectionBlocks[(int)ecl, version];
        }

        static byte[] ReedSolomonDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] = (byte)ReedSolomonMultiply(result[j], root);
                    if (j + 1 < result.Length)
                        result[j] ^= result[j + 1];
                }
                root = ReedSolomonMultiply(root, 0x02);
            }
            return result;
        }

        static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++)
                    result[i] ^= (byte)ReedSolomonMultiply(divisor[i], factor);
            }
            return result;
        }

        static int ReedSolomonMultiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        static bool Bit(int x, int i)
        {
            return ((x >> i) & 1) != 0;
        }

        // Indexed by [Ecc, version]; version 0 is unused.
        static readonly int[,] EccCodewordsPerBlock =
        {
            { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
            { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        };

        static readonly int[,] NumErrorCorrectionBlocks =
        {
            { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
            { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
            { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
            { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
        };
    }
}
