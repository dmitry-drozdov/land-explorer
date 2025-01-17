using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using TrendMicro.Tlsh;
using System.Net.Http.Headers;

namespace Land.Markup.Binding
{
    // TODO: нерабочий метод
    public static class FuzzyHashing
    {
        public const int MIN_TEXT_LENGTH = 25;
        public const int TLSH_HASH_LENGTH = 138;


        public static byte[] GetFuzzyHash(string text)
        {
            if (Encoding.UTF8.GetByteCount(text)<512)
            {
                //throw new Exception($"{Encoding.UTF8.GetByteCount(text)}");
                return new byte[] { };
            }
            var t = new Tlsh();
            t.Update(Encoding.Unicode.GetBytes(text));
            var res = t.GetHash();
            return res.ToByteArray().ToArray();
        }

        public static double CompareTexts(string txt1, string txt2)
        {
            var hash1 = GetFuzzyHash(txt1);
            var hash2 = GetFuzzyHash(txt2);

            return CompareHashes(hash1, hash2);
        }

        public static double CompareHashes(byte[] hash1, byte[] hash2)
        {
            var d = ComputeHashDistance(hash1, hash2);
            return Math.Max(0, (300 - d) / 300.0);
        }


        static int ComputeHashDistance(byte[] hash1, byte[] hash2)
        {
            if (hash1.Length != hash2.Length)
            {
                return Math.Max(hash1.Length, hash2.Length);
            }

            int distance = 0;
            for (int i = 0; i < hash1.Length; i++)
            {
                if (hash1[i] != hash2[i])
                {
                    distance++;
                }
            }
            return distance;
        }


    }
}
