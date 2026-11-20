using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Pure C# dependency-free QR Code generator (ISO/IEC 18004 compliant).
    /// Supports Byte mode, Error Correction Level M, and Versions 1 through 7.
    /// Outputs standard WPF BitmapSource ready for data binding and clipboard export.
    /// </summary>
    public static class QrCodeService
    {
        #region Wi-Fi URI Formatter

        /// <summary>
        /// Formats standard Wi-Fi configuration URI (ZXing / MeCard format)
        /// used by Android, iOS Camera, and Windows.
        /// </summary>
        public static string FormatWifiPayload(string ssid, string password, string authType = "WPA", bool isHidden = false)
        {
            if (string.IsNullOrEmpty(ssid)) ssid = "Hotspot";
            string encSsid = EscapeWifiString(ssid);
            string hiddenTag = isHidden ? "H:true;" : string.Empty;

            if (string.IsNullOrEmpty(password) || authType.Equals("Open", StringComparison.OrdinalIgnoreCase))
            {
                return $"WIFI:T:nopass;S:{encSsid};{hiddenTag};";
            }

            string encPass = EscapeWifiString(password);
            string type = authType.Contains("WPA3", StringComparison.OrdinalIgnoreCase) ? "WPA" : "WPA";
            return $"WIFI:T:{type};S:{encSsid};P:{encPass};{hiddenTag};";
        }

        private static string EscapeWifiString(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            var sb = new StringBuilder(str.Length + 8);
            foreach (char c in str)
            {
                if (c is '\\' or ';' or ',' or ':' or '"')
                {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        #endregion

        #region QR Code Version Specs (Level M)

        private struct VersionSpec
        {
            public int Version;
            public int Dimension;
            public int TotalCodewords;
            public int DataCodewords;
            public int EcCodewordsPerBlock;
            public int NumBlocks;
            public int[] AlignmentCoords;
        }

        private static readonly VersionSpec[] VersionTable = new VersionSpec[]
        {
            new VersionSpec { Version = 1, Dimension = 21, TotalCodewords = 26,  DataCodewords = 16,  EcCodewordsPerBlock = 10, NumBlocks = 1, AlignmentCoords = Array.Empty<int>() },
            new VersionSpec { Version = 2, Dimension = 25, TotalCodewords = 44,  DataCodewords = 28,  EcCodewordsPerBlock = 16, NumBlocks = 1, AlignmentCoords = new[] { 6, 18 } },
            new VersionSpec { Version = 3, Dimension = 29, TotalCodewords = 70,  DataCodewords = 44,  EcCodewordsPerBlock = 26, NumBlocks = 1, AlignmentCoords = new[] { 6, 22 } },
            new VersionSpec { Version = 4, Dimension = 33, TotalCodewords = 100, DataCodewords = 64,  EcCodewordsPerBlock = 18, NumBlocks = 2, AlignmentCoords = new[] { 6, 26 } },
            new VersionSpec { Version = 5, Dimension = 37, TotalCodewords = 134, DataCodewords = 86,  EcCodewordsPerBlock = 24, NumBlocks = 2, AlignmentCoords = new[] { 6, 30 } },
            new VersionSpec { Version = 6, Dimension = 41, TotalCodewords = 172, DataCodewords = 108, EcCodewordsPerBlock = 16, NumBlocks = 4, AlignmentCoords = new[] { 6, 34 } },
            new VersionSpec { Version = 7, Dimension = 45, TotalCodewords = 196, DataCodewords = 124, EcCodewordsPerBlock = 18, NumBlocks = 4, AlignmentCoords = new[] { 6, 22, 38 } }
        };

        #endregion

        #region Galois Field & Reed-Solomon Math

        private static readonly byte[] ExpTable = new byte[512];
        private static readonly byte[] LogTable = new byte[256];

        static QrCodeService()
        {
            int val = 1;
            for (int i = 0; i < 255; i++)
            {
                ExpTable[i] = (byte)val;
                ExpTable[i + 255] = (byte)val;
                LogTable[val] = (byte)i;
                val <<= 1;
                if ((val & 0x100) != 0)
                {
                    val ^= 0x11D; // Primitive polynomial x^8 + x^4 + x^3 + x^2 + 1
                }
            }
            LogTable[0] = 0;
        }

        private static byte GfMultiply(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return ExpTable[LogTable[a] + LogTable[b]];
        }

        private static byte[] GenerateRsPolynomial(int ecCount)
        {
            byte[] poly = new byte[] { 1 };
            for (int i = 0; i < ecCount; i++)
            {
                byte factor = ExpTable[i];
                byte[] next = new byte[poly.Length + 1];
                for (int j = 0; j < poly.Length; j++)
                {
                    next[j] ^= GfMultiply(poly[j], factor);
                    next[j + 1] ^= poly[j];
                }
                poly = next;
            }
            Array.Reverse(poly);
            return poly;
        }

        private static byte[] CalculateReedSolomon(byte[] data, int ecCount)
        {
            byte[] gen = GenerateRsPolynomial(ecCount);
            byte[] rem = new byte[ecCount];

            foreach (byte b in data)
            {
                byte lead = (byte)(b ^ rem[0]);
                for (int j = 0; j < ecCount - 1; j++)
                {
                    rem[j] = (byte)(rem[j + 1] ^ GfMultiply(gen[ecCount - 1 - j], lead));
                }
                rem[ecCount - 1] = GfMultiply(gen[0], lead);
            }
            return rem;
        }

        #endregion

        #region Matrix Generator

        /// <summary>
        /// Generates a boolean 2D matrix representing the QR Code modules (true = dark, false = light).
        /// </summary>
        public static bool[,]? GenerateMatrix(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return null;
            byte[] textBytes = Encoding.UTF8.GetBytes(payload);

            // Find smallest version that accommodates the payload
            VersionSpec? chosenSpec = null;
            foreach (var spec in VersionTable)
            {
                // In Byte mode: 4 bits mode + 8 bits char count + data bytes <= data codewords * 8
                int requiredBits = 4 + 8 + (textBytes.Length * 8);
                if (requiredBits <= spec.DataCodewords * 8)
                {
                    chosenSpec = spec;
                    break;
                }
            }

            if (!chosenSpec.HasValue) return null; // Payload exceeds Version 7 limit
            var vs = chosenSpec.Value;

            // 1. Bit buffer encoding: Mode (0100) + Char Count (8 bits) + Data Bytes
            var bitList = new List<bool>(vs.DataCodewords * 8);
            AppendBits(bitList, 0b0100, 4); // Byte mode
            AppendBits(bitList, textBytes.Length, 8); // Count
            foreach (byte b in textBytes)
            {
                AppendBits(bitList, b, 8);
            }

            // Terminator: up to 4 zero bits
            int remainingBits = (vs.DataCodewords * 8) - bitList.Count;
            int termLen = Math.Min(4, remainingBits);
            AppendBits(bitList, 0, termLen);

            // Byte boundary padding
            while ((bitList.Count % 8) != 0)
            {
                bitList.Add(false);
            }

            // Convert bit list to bytes
            byte[] dataBytes = new byte[vs.DataCodewords];
            int byteIndex = 0;
            for (int i = 0; i < bitList.Count; i += 8)
            {
                byte b = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    if (bitList[i + bit]) b |= (byte)(1 << (7 - bit));
                }
                dataBytes[byteIndex++] = b;
            }

            // Pad alternating bytes 0xEC and 0x11
            byte[] padBytes = new byte[] { 0xEC, 0x11 };
            int padIdx = 0;
            while (byteIndex < vs.DataCodewords)
            {
                dataBytes[byteIndex++] = padBytes[padIdx++ % 2];
            }

            // 2. Reed-Solomon Error Correction Codewords calculation & interleaving
            int numBlocks = vs.NumBlocks;
            int dataPerBlock = vs.DataCodewords / numBlocks;
            int ecPerBlock = vs.EcCodewordsPerBlock;

            byte[][] dataBlocks = new byte[numBlocks][];
            byte[][] ecBlocks = new byte[numBlocks][];
            for (int b = 0; b < numBlocks; b++)
            {
                dataBlocks[b] = new byte[dataPerBlock];
                Array.Copy(dataBytes, b * dataPerBlock, dataBlocks[b], 0, dataPerBlock);
                ecBlocks[b] = CalculateReedSolomon(dataBlocks[b], ecPerBlock);
            }

            // Interleave data codewords, then error correction codewords
            byte[] finalCodewords = new byte[vs.TotalCodewords];
            int finalIdx = 0;
            for (int i = 0; i < dataPerBlock; i++)
            {
                for (int b = 0; b < numBlocks; b++)
                {
                    finalCodewords[finalIdx++] = dataBlocks[b][i];
                }
            }
            for (int i = 0; i < ecPerBlock; i++)
            {
                for (int b = 0; b < numBlocks; b++)
                {
                    finalCodewords[finalIdx++] = ecBlocks[b][i];
                }
            }

            // 3. Matrix construction
            int dim = vs.Dimension;
            bool[,] matrix = new bool[dim, dim];
            bool[,] reserved = new bool[dim, dim];

            // 3a. Finder patterns & separators
            PlaceFinderPattern(matrix, reserved, 0, 0, dim);
            PlaceFinderPattern(matrix, reserved, 0, dim - 7, dim);
            PlaceFinderPattern(matrix, reserved, dim - 7, 0, dim);

            // 3b. Alignment patterns
            if (vs.AlignmentCoords.Length > 0)
            {
                var coords = vs.AlignmentCoords;
                for (int r = 0; r < coords.Length; r++)
                {
                    for (int c = 0; c < coords.Length; c++)
                    {
                        int row = coords[r];
                        int col = coords[c];
                        // Skip if overlaps finder or separator
                        if (reserved[row, col]) continue;
                        PlaceAlignmentPattern(matrix, reserved, row, col);
                    }
                }
            }

            // 3c. Timing patterns
            for (int i = 8; i < dim - 8; i++)
            {
                bool isDark = (i % 2) == 0;
                if (!reserved[6, i]) { matrix[6, i] = isDark; reserved[6, i] = true; }
                if (!reserved[i, 6]) { matrix[i, 6] = isDark; reserved[i, 6] = true; }
            }

            // 3d. Dark module
            matrix[4 * vs.Version + 9, 8] = true;
            reserved[4 * vs.Version + 9, 8] = true;

            // 3e. Reserve format info modules
            for (int i = 0; i < 9; i++)
            {
                reserved[8, i] = true;
                reserved[i, 8] = true;
            }
            for (int i = 0; i < 8; i++)
            {
                reserved[8, dim - 1 - i] = true;
                reserved[dim - 1 - i, 8] = true;
            }

            // 3f. Version 7+ needs two 6x3 version-information blocks.
            if (vs.Version >= 7)
            {
                for (int i = 0; i < 18; i++)
                {
                    int a = dim - 11 + (i % 3);
                    int b = i / 3;
                    reserved[b, a] = true;
                    reserved[a, b] = true;
                }
            }

            // 4. Place data bits in 2-column strips with Mask 0 ((row + col) % 2 == 0)
            int bitIdx = 0;
            int totalBits = finalCodewords.Length * 8;
            bool upwards = true;

            for (int col = dim - 1; col > 0; col -= 2)
            {
                if (col == 6) col--; // Skip timing column
                for (int step = 0; step < dim; step++)
                {
                    int row = upwards ? (dim - 1 - step) : step;
                    for (int c = 0; c < 2; c++)
                    {
                        int currentCol = col - c;
                        if (reserved[row, currentCol]) continue;

                        bool bit = false;
                        if (bitIdx < totalBits)
                        {
                            int bytePos = bitIdx / 8;
                            int bitPos = 7 - (bitIdx % 8);
                            bit = ((finalCodewords[bytePos] >> bitPos) & 1) == 1;
                            bitIdx++;
                        }

                        // Apply Mask 0: (row + col) % 2 == 0
                        bool mask = ((row + currentCol) % 2) == 0;
                        matrix[row, currentCol] = bit ^ mask;
                    }
                }
                upwards = !upwards;
            }

            // 5. Write Format Information (Level M = 00, Mask 0 = 000 -> 0x5412 XOR)
            // Precomputed 15-bit format for Level M + Mask 0: 0x5412 (0b101010000010010)
            int formatBits = 0x5412;
            WriteFormatInfo(matrix, dim, formatBits);

            return matrix;
        }

        private static void AppendBits(List<bool> list, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                list.Add(((value >> i) & 1) == 1);
            }
        }

        private static void PlaceFinderPattern(bool[,] matrix, bool[,] reserved, int startRow, int startCol, int dim)
        {
            // 7x7 Finder Pattern with 1-module quiet separator
            for (int r = -1; r <= 7; r++)
            {
                for (int c = -1; c <= 7; c++)
                {
                    int row = startRow + r;
                    int col = startCol + c;
                    if (row < 0 || row >= dim || col < 0 || col >= dim) continue;
                    reserved[row, col] = true;

                    if (r >= 0 && r < 7 && c >= 0 && c < 7)
                    {
                        bool isBorder = (r == 0 || r == 6 || c == 0 || c == 6);
                        bool isCenter = (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                        matrix[row, col] = isBorder || isCenter;
                    }
                    else
                    {
                        matrix[row, col] = false; // Separator
                    }
                }
            }
        }

        private static void PlaceAlignmentPattern(bool[,] matrix, bool[,] reserved, int centerRow, int centerCol)
        {
            for (int r = -2; r <= 2; r++)
            {
                for (int c = -2; c <= 2; c++)
                {
                    int row = centerRow + r;
                    int col = centerCol + c;
                    reserved[row, col] = true;
                    bool isBorder = (Math.Abs(r) == 2 || Math.Abs(c) == 2);
                    bool isCenter = (r == 0 && c == 0);
                    matrix[row, col] = isBorder || isCenter;
                }
            }
        }

        private static void WriteFormatInfo(bool[,] matrix, int dim, int format)
        {
            // Format info consists of 15 bits
            // Placed around top-left, top-right, and bottom-left finders
            int[] bits = new int[15];
            for (int i = 0; i < 15; i++)
            {
                bits[i] = (format >> i) & 1;
            }

            // Top-left:
            // bits 0..5: (8,0), (8,1), (8,2), (8,3), (8,4), (8,5)
            // bit 6: (8,7)
            // bit 7: (8,8)
            // bit 8: (7,8)
            // bits 9..14: (5,8), (4,8), (3,8), (2,8), (1,8), (0,8)
            matrix[8, 0] = bits[0] == 1;
            matrix[8, 1] = bits[1] == 1;
            matrix[8, 2] = bits[2] == 1;
            matrix[8, 3] = bits[3] == 1;
            matrix[8, 4] = bits[4] == 1;
            matrix[8, 5] = bits[5] == 1;
            matrix[8, 7] = bits[6] == 1;
            matrix[8, 8] = bits[7] == 1;
            matrix[7, 8] = bits[8] == 1;
            matrix[5, 8] = bits[9] == 1;
            matrix[4, 8] = bits[10] == 1;
            matrix[3, 8] = bits[11] == 1;
            matrix[2, 8] = bits[12] == 1;
            matrix[1, 8] = bits[13] == 1;
            matrix[0, 8] = bits[14] == 1;

            // Bottom-left: bits 8..14 at rows dim-1 down to dim-7 (dark module sits at dim-8)
            for (int i = 0; i < 7; i++)
            {
                matrix[dim - 1 - i, 8] = bits[8 + i] == 1;
            }

            // Top-right: bits 0..7 at columns dim-1 down to dim-8
            for (int i = 0; i < 8; i++)
            {
                matrix[8, dim - 1 - i] = bits[i] == 1;
            }

            // The module at (dim-8, 8) is always dark and must survive format writing.
            matrix[dim - 8, 8] = true;

            // Version 7+ version information (0x07C94 for version 7, mask independent).
            if (dim >= 45)
            {
                const int versionInfo = 0x07C94;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((versionInfo >> i) & 1) == 1;
                    int a = dim - 11 + (i % 3);
                    int b = i / 3;
                    matrix[b, a] = bit;
                    matrix[a, b] = bit;
                }
            }
        }

        #endregion

        #region WPF Bitmap Rendering

        /// <summary>
        /// Converts the QR matrix to a high-contrast WPF BitmapSource suitable for display,
        /// saving, or copying to clipboard.
        /// </summary>
        public static BitmapSource? GenerateBitmap(string payload, int pixelSize = 220)
        {
            var matrix = GenerateMatrix(payload);
            if (matrix == null) return null;

            int dim = matrix.GetLength(0);
            const int quietZone = 4; // 4 modules quiet zone
            int totalModules = dim + (quietZone * 2);
            int scale = Math.Max(1, pixelSize / totalModules);
            int pixelWidth = totalModules * scale;
            int pixelHeight = totalModules * scale;

            // Format32bppPbgra: 4 bytes per pixel (B, G, R, A)
            int stride = pixelWidth * 4;
            byte[] pixels = new byte[pixelHeight * stride];

            // Palette: dark module #0B111E (Navy/Black), light module #FFFFFF (Crisp White)
            byte darkB = 0x1E, darkG = 0x11, darkR = 0x0B, darkA = 0xFF;
            byte lightB = 0xFF, lightG = 0xFF, lightR = 0xFF, lightA = 0xFF;

            for (int y = 0; y < pixelHeight; y++)
            {
                int modY = (y / scale) - quietZone;
                for (int x = 0; x < pixelWidth; x++)
                {
                    int modX = (x / scale) - quietZone;
                    bool isDark = (modY >= 0 && modY < dim && modX >= 0 && modX < dim) && matrix[modY, modX];

                    int offset = (y * stride) + (x * 4);
                    if (isDark)
                    {
                        pixels[offset + 0] = darkB;
                        pixels[offset + 1] = darkG;
                        pixels[offset + 2] = darkR;
                        pixels[offset + 3] = darkA;
                    }
                    else
                    {
                        pixels[offset + 0] = lightB;
                        pixels[offset + 1] = lightG;
                        pixels[offset + 2] = lightR;
                        pixels[offset + 3] = lightA;
                    }
                }
            }

            var bitmap = BitmapSource.Create(
                pixelWidth, pixelHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze(); // Freezing allows safe cross-thread WPF data binding
            return bitmap;
        }

        #endregion
    }
}
