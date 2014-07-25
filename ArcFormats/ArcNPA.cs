//! \file       ArcNPA.cs
//! \date       Fri Jul 18 04:07:42 2014
//! \brief      NPA archive format implementation.
//

using System;
using System.IO;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ZLibNet;
using GameRes.Formats.Strings;
using GameRes.Formats.Properties;

namespace GameRes.Formats
{
    public class NpaEntry : PackedEntry
    {
        public byte[] RawName;
    }

    public class NpaArchive : ArcFile
    {
        public NpaTitleId   GameId   { get; private set; }
        public int          Key      { get; private set; }
        public byte[]       KeyTable { get { return m_key_table.Value; } }

        private Lazy<byte[]> m_key_table;

        public NpaArchive (ArcView arc, ArchiveFormat impl, ICollection<Entry> dir,
                           NpaTitleId game_id, int key)
            : base (arc, impl, dir)
        {
            GameId   = game_id;
            Key      = key;
            m_key_table = new Lazy<byte[]> (() => NpaOpener.GenerateKeyTable (game_id));
        }
    }

    public enum NpaTitleId
    {
        CHAOSHEAD = 0, CHAOSHEADTR1, CHAOSHEADTR2, MURAMASATR, MURAMASA, SUMAGA, DJANGO, DJANGOTR,
        LAMENTO, LAMENTOTR, SWEETPOOL, SUMAGASP, DEMONBANE, MURAMASAAD, AXANAEL, KIKOKUGAI, SONICOMITR2,
        SUMAGA3P, SONICOMI, LOSTX, LOSTXTRAILER, DRAMATICALMURDER, TOTONO, PHENOMENO, NEKODA,
        NotEncrypted
    }

    [Export(typeof(ArchiveFormat))]
    public class NpaOpener : ArchiveFormat
    {
        public override string Tag { get { return "NPA"; } }
        public override string Description { get { return arcStrings.NPADescription; } }
        public override uint Signature { get { return 0x0141504e; } } // NPA\x01
        public override bool IsHierarchic { get { return true; } }

        /// <summary>Known encryption schemes.</summary>
        public static readonly string[] KnownSchemes = new string[] {
            "Chaos;Head", "Chaos;Head Trial 1", "Chaos;Head Trial 2", "Muramasa Trial", "Muramasa",
            "Sumaga", "Zoku Satsuriku no Django", "Zoku Satsuriku no Django Trial", "Lamento",
            "Lamento Trial", "Sweet Pool", "Sumaga Special", "Demonbane", "MuramasaAD", "Axanael",
            "Kikokugai", "Sonicomi Trial 2", "Sumaga 3% Trial", "Sonicomi Version 1.0",
            "Guilty Crown Lost Xmas", "Guilty Crown Lost Xmas Trailer", "DRAMAtical Murder",
            "Kimi to Kanojo to Kanojo no Koi", "Phenomeno", "Nekoda -Nyanda-",
            arcStrings.ArcNoEncryption,
        };

        public override ArcFile TryOpen (ArcView file)
        {
            int key1 = file.View.ReadInt32 (7);
            int key2 = file.View.ReadInt32 (11);
            bool compressed = 0 != file.View.ReadByte (15);
            bool encrypted  = 0 != file.View.ReadByte (16);
    		int total_count = file.View.ReadInt32 (17);
		    int folder_count = file.View.ReadInt32 (21);
		    int file_count = file.View.ReadInt32 (25);
            if (total_count < folder_count + file_count)
                return null;
		    uint dir_size = file.View.ReadUInt32 (37);
            if (dir_size >= file.MaxOffset)
                return null;

            var game_id = NpaTitleId.NotEncrypted;
            if (encrypted)
                game_id = QueryGameEncryption();

            int key;
            if (encrypted && (game_id == NpaTitleId.LAMENTO || game_id == NpaTitleId.LAMENTOTR))
                key = key1 + key2;
            else
                key = key1 * key2;

            long cur_offset = 41;
            var dir = new List<Entry> (file_count);
            for (int i = 0; i < total_count; ++i)
            {
                int name_size = file.View.ReadInt32 (cur_offset);
                int type = file.View.ReadByte (cur_offset+4+name_size);
                if (1 != type) // ignore directory entries
                {
                    var raw_name = new byte[name_size];
                    file.View.Read (cur_offset+4, raw_name, 0, (uint)name_size);
                    for (int x = 0; x < name_size; ++x)
                        raw_name[x] += DecryptName (x, i, key);
                    var info_offset = cur_offset + 5 + name_size;

                    uint id = file.View.ReadUInt32 (info_offset);
                    uint offset = file.View.ReadUInt32 (info_offset+4);
                    uint size = file.View.ReadUInt32 (info_offset+8);
                    uint unpacked_size = file.View.ReadUInt32 (info_offset+12);

                    var entry = new NpaEntry {
                        Name        = Encodings.cp932.GetString (raw_name),
                        Offset      = dir_size+offset+41,
                        Size        = size,
                        UnpackedSize = unpacked_size,
                        IsPacked    = compressed,
                        RawName     = raw_name,
                    };
                    entry.Type = FormatCatalog.Instance.GetTypeFromName (entry.Name);
                    dir.Add (entry);
                }
                cur_offset += 4 + name_size + 17;
            }
            if (game_id != NpaTitleId.NotEncrypted)
                return new NpaArchive (file, this, dir, game_id, key);
            else
                return new ArcFile (file, this, dir);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            if (arc is NpaArchive && entry is NpaEntry)
                return OpenEncryptedEntry (arc as NpaArchive, entry as NpaEntry);

            var input = arc.File.CreateStream (entry.Offset, entry.Size);
            return UnpackEntry (input, entry);
        }

        private Stream UnpackEntry (Stream input, Entry entry)
        {
            if (entry.Type != "image")
            {
                var npa_entry = entry as PackedEntry;
                if (null != npa_entry && npa_entry.IsPacked)
                    return new ZLibStream (input, CompressionMode.Decompress);
            }
            return input;
        }

        private Stream OpenEncryptedEntry (NpaArchive arc, NpaEntry entry)
        {
            int key = GetKeyFromEntry (entry, arc.GameId, arc.Key);
            int encrypted_length = 0x1000;

            if (arc.GameId != NpaTitleId.LAMENTO && arc.GameId != NpaTitleId.LAMENTOTR)
                encrypted_length += entry.RawName.Length;

            using (var view = arc.File.CreateViewAccessor (entry.Offset, entry.Size))
            {
                byte[] buffer = new byte[entry.Size];
                unsafe
                {
                    byte* src = view.GetPointer (entry.Offset);
                    try
                    {
                        int x;
                        for (x = 0; x < entry.Size && x < encrypted_length; x++)
                        {
                            if (arc.GameId == NpaTitleId.LAMENTO || arc.GameId == NpaTitleId.LAMENTOTR)
                            {
                                buffer[x] = (byte)(arc.KeyTable[src[x]] - key);
                            }
                            else if (arc.GameId == NpaTitleId.TOTONO)
                            {
                                byte r = src[x];
                                r = arc.KeyTable[r];
                                r = arc.KeyTable[r];
                                r = arc.KeyTable[r];
                                r = (byte)~r;
                                buffer[x] = (byte)((sbyte)r - key - x);
                            }
                            else
                            {
                                buffer[x] = (byte)(arc.KeyTable[src[x]] - key - x);
                            }
                        }
                        if (x != entry.Size)
                            Marshal.Copy ((IntPtr)(src+x), buffer, x, (int)(entry.Size-x));
                    } finally {
                        view.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                }
                return UnpackEntry (new MemoryStream (buffer, false), entry);
            }
        }

        byte DecryptName (int index, int curfile, int arc_key)
        {
            int key = 0xFC*index;

            key -= arc_key >> 0x18;
            key -= arc_key >> 0x10;
            key -= arc_key >> 0x08;
            key -= arc_key  & 0xff;

            key -= curfile >> 0x18;
            key -= curfile >> 0x10;
            key -= curfile >> 0x08;
            key -= curfile;

            return (byte)(key & 0xff);
        }

        byte GetKeyFromEntry (NpaEntry entry, NpaTitleId game_id, int key2)
        {
            int key1;
            switch (game_id)
            {
                case NpaTitleId.AXANAEL:
                case NpaTitleId.KIKOKUGAI:
                case NpaTitleId.SONICOMITR2:
                case NpaTitleId.SONICOMI:
                case NpaTitleId.LOSTX:
                case NpaTitleId.DRAMATICALMURDER:
                    key1 = 0x20101118;
                    break;
                case NpaTitleId.TOTONO:
                    key1 = 0x12345678;
                    break;
                default:
                    key1 = unchecked((int)0x87654321);
                    break;
            }
            var name = entry.RawName;
            int i;
            for (i = 0; i < name.Length; ++i)
                key1 -= name[i];

            int key = key1 * i;

            if (game_id != NpaTitleId.LAMENTO && game_id != NpaTitleId.LAMENTOTR) // if the game is not Lamento
            {
                key += key2;
                key *= (int)entry.UnpackedSize;
            }
            return (byte)(key & 0xff);
        }

        public static byte[] GenerateKeyTable (NpaTitleId title_id)
        {
            if ((int)title_id >= OrderTable.Length)
                throw new ArgumentOutOfRangeException ("title_id", "Invalid title id specified");

            byte[] order = OrderTable[(int)title_id];
            var table = new byte[256];
            for (int i = 0; i < 256; ++i)
            {
                int edx = i << 4;
                int dl = (edx + order[i & 0x0f]) & 0xff;
                int dh = (edx + (order[i>>4] << 8)) & 0xff00;
                edx = (dh | dl) >> 4;
                var eax = BaseTable[i];
                table[eax] = (byte)(edx & 0xff);
            }
            for (int i = 16; i+1 < order.Length; i+=2)
            {
                int ecx = order[i];
                int edx = order[i+1];
                byte tmp = table[ecx];
                table[ecx] = table[edx];
                table[edx] = tmp;
            }
            return table;
        }

        NpaTitleId QueryGameEncryption ()
        {
            var widget = new GUI.WidgetNPA (KnownSchemes[(int)Settings.Default.NPAScheme]);
            var args = new ParametersRequestEventArgs
            {
                Notice = arcStrings.ArcEncryptedNotice,
                InputWidget = widget,
            };
            FormatCatalog.Instance.InvokeParametersRequest (this, args);
            if (!args.InputResult)
                throw new OperationCanceledException();

            NpaTitleId scheme = GetTitleId (widget.GetScheme());
            Settings.Default.NPAScheme = scheme;
            return scheme;
        }

        public static NpaTitleId GetTitleId (string title)
        {
            var index = Array.IndexOf (KnownSchemes, title);
            if (index != -1)
                return (NpaTitleId)index;
            else
                return NpaTitleId.NotEncrypted;
        }

        static readonly byte[] BaseTable = {
            0x6F,0x05,0x6A,0xBF,0xA1,0xC7,0x8E,0xFB,0xD4,0x2F,0x80,0x58,0x4A,0x17,0x3B,0xB1,
            0x89,0xEC,0xA0,0x9F,0xD3,0xFC,0xC2,0x04,0x68,0x03,0xF3,0x25,0xBE,0x24,0xF1,0xBD,
            0xB8,0x41,0xC9,0x27,0x0E,0xA3,0xD8,0x7F,0x5B,0x8F,0x16,0x49,0xAA,0xB2,0x18,0xA7,
            0x33,0xE4,0xDB,0x48,0xCA,0xDE,0xAE,0xCD,0x13,0x1F,0x15,0x2E,0x39,0xF5,0x1E,0xDD,
            0x0F,0x88,0x4C,0x98,0x36,0xB4,0x3F,0x09,0x83,0xFD,0x32,0xBA,0x14,0x30,0x7A,0x63,
            0xB9,0x56,0x95,0x61,0xCC,0x8B,0xEF,0xDA,0xE5,0x2C,0xDC,0x12,0x1A,0x67,0x23,0x50,
            0xD1,0xC3,0x7E,0x6D,0xB6,0x90,0x3C,0xB3,0x0B,0xE2,0x91,0x70,0xA8,0xDF,0x44,0xC4,
            0xF4,0x01,0x5C,0x10,0x06,0xE7,0x54,0x40,0x43,0x72,0x38,0xBC,0xE3,0x07,0xFA,0x34,
            0x02,0xA4,0xF7,0x74,0xA9,0x4D,0x42,0xA5,0x85,0x35,0x79,0xD2,0x76,0x97,0x45,0x4F,
            0x08,0x5A,0xB0,0xEE,0x51,0x73,0x69,0x9E,0x94,0x47,0x77,0x29,0xD9,0x64,0x11,0xEB,
            0x37,0xAC,0x20,0x62,0x9A,0x6B,0x9C,0x75,0x22,0x87,0xAB,0x78,0x53,0xC8,0x5D,0xAD,
            0x2A,0xF2,0xCB,0xB7,0x0D,0xED,0x86,0x55,0xFF,0x19,0x57,0xD7,0xD5,0x60,0xC6,0x3D,
            0xEA,0xC1,0x6C,0xE1,0xC0,0x65,0x84,0xC5,0xE0,0x3E,0x7D,0x28,0x66,0xAF,0x1C,0x9B,
            0xCF,0x81,0x4E,0x26,0x59,0x2B,0x5F,0x7B,0xE8,0x8D,0x52,0x7C,0xF8,0x82,0x0C,0xF9,
            0x8C,0xE9,0xB5,0xE6,0x31,0x93,0x46,0x5E,0x1D,0x1B,0x4B,0x71,0xD6,0x92,0x3A,0xA6,
            0x2D,0x00,0x9D,0xBB,0x6E,0xF0,0x99,0xCE,0x21,0x0A,0xD0,0xF6,0xFE,0xA2,0x8A,0x96,
        };

        static readonly byte[][] OrderTable = {
            // CHAOSHEAD
            new byte[] { 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 },
            // CHAOSHEADTR1
            new byte[] { 0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0x1e,0x4e,0x66,0xb6 },
            // CHAOSHEADTR2
            new byte[] { 0x05,0x05,0x05,0x05,0x05,0x0b,0x0b,0x0b,0x0b,0x0b,0x00,0x00,0x00,0x00,0x00,0x00 },
            // MURAMASATR
            new byte[] { 0x3c,0xe0,0x2e,0x2f,0x20,0x2e,0x2f,0x20,0x8e,0x80,0x80,0xf2,0xf2,0xf2,0xfa,0xfc },
            // MURAMASA
            new byte[] { 0x35,0x70,0x2e,0x66,0x67,0x65,0x66,0x67,0x85,0x89,0x89,0x3b,0x3b,0x8b,0x81,0x85 },
            // SUMAGA
            new byte[] { 0x3c,0xe0,0x2e,0x2f,0x2f,0x2f,0x2f,0x20,0x8e,0x8f,0x8f,0xf2,0xf2,0xf2,0xfc,0xfc },
            // DJANGO
            new byte[] { 0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0x1e,0x4e,0x66,0xb6 },
            // DJANGOTR
            new byte[] { 0xed,0xee,0xee,0xef,0xed,0xee,0xee,0xee,0xfe,0xde,0xee,0xef,0xed,0xee,0xfe,0xdf,0x1e,0x4e,0x66,0xb6 },
            // LAMENTO
            new byte[] { 0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0x1e,0x4e,0x66,0xb6 },
            // LAMENTOTR
            new byte[] { 0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0xee,0x1e,0x4e,0x66,0xb6 },
            // SWEETPOOL
            new byte[] { 0x38,0x9c,0x2a,0x8b,0x8b,0x8b,0x8b,0x8c,0x8a,0x8b,0x8b,0xae,0xae,0xae,0xa8,0xa8 },
            // SUMAGASP
            new byte[] { 0xab,0x6f,0x9d,0x9e,0x9f,0x9d,0x9e,0xaf,0x8d,0xff,0xff,0x71,0x71,0x71,0x79,0x7b },
            // DEMONBANE
            new byte[] { 0x96,0xb9,0x47,0x48,0x99,0x97,0x9c,0xaa,0x88,0xca,0xea,0x73,0x73,0x7b,0xc9,0xc6 },
            // MURAMASAAD
            new byte[] { 0x00,0x04,0x04,0x68,0x68,0x68,0x68,0x68,0x6f,0x6f,0x9f,0x96,0x96,0x96,0x96,0x9b },
            // AXANAEL
            new byte[] { 0x08,0x0c,0x0c,0xc0,0xf0,0xf0,0xf0,0xf0,0xf7,0xf7,0xf7,0xfe,0xfe,0xfe,0xfe,0xf3 },
            // KIKOKUGAI
            new byte[] { 0x0f,0x07,0x07,0x90,0xf7,0xf7,0xf7,0xf7,0xf2,0x47,0x47,0x49,0xc9,0xc9,0xc9,0xc3 },
            // SONICOMITR2
            new byte[] { 0x08,0x0a,0x0a,0x40,0xfa,0xfa,0x50,0x50,0x55,0xf7,0xf7,0xf9,0x29,0x2c,0x7c,0x73 },
            // SUMAGA3P
            new byte[] { 0x0f,0xef,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff },
            // SONICOMI
            new byte[] { 0x0e,0x0b,0x0e,0x77,0x2e,0x2e,0x80,0x86,0xb9,0x2e,0x2e,0x29,0x89,0x82,0xad,0xaa },
            // LOSTX
            new byte[] { 0x38,0xba,0x4b,0x5b,0x55,0xae,0xee,0xe0,0x67,0x48,0x08,0x0a,0x6a,0x3d,0x32,0x8d },
            // LOSTXTRAILER
            new byte[] { 0x34,0x7a,0xbb,0xcb,0x11,0x65,0xea,0x5c,0x27,0x0f,0xcf,0xc6,0x66,0x39,0x39,0xfd },
            // DRAMATICALMURDER
            new byte[] { 0x05,0x0d,0x0d,0x13,0xb5,0x3d,0x8d,0x2d,0x20,0xc7,0xc7,0xcf,0x1f,0xef,0xef,0x48 },
            // TOTONO
            new byte[] { 0x6e,0x60,0x90,0xac,0xb3,0xe3,0x83,0xd6,0xde,0x7a,0x7a,0x7f,0xef,0xbf,0xb2,0xd6 },
            // PHENOMENO
            new byte[] { 0x30,0x96,0xdb,0x2b,0x3d,0x81,0x02,0x74,0x47,0x2b,0xeb,0xee,0x6e,0x35,0x35,0x5d },
            // NEKODA
            new byte[] { 0xdc,0xdc,0xec,0xcd,0xdb,0xdc,0xdc,0xdc,0xdc,0xdc,0xdc,0xdc,0xdc,0xdc,0xdc,0xdc,0x1e,0x4e,0x66,0xb6 },
        };
    }
}