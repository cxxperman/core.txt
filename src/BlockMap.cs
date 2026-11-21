// core.txt. BlockMap.cs — обновление большого файла по кусочкам.
// electron-builder кладёт рядом с установщиком карту блоков: файл разрезан на куски, у каждого своя контрольная сумма.
// Сравнив карты прежней и новой версии, видно, какие куски не изменились: их можно взять из прежнего файла, который
// core.txt держит в запасе, а из сети докачать только остальное. Готовый файл всё равно сверяется с SHA-256 из релиза:
// любая неувязка — и core.txt молча качает файл целиком, как раньше.
// Всё, что можно посчитать без сети, — здесь, чистыми функциями; сеть — тонкая обёртка в Releases.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DevTxt.Core
{
    class BlockMap
    {
        public long Offset;     // где в файле начинается первый блок
        public long[] Sizes;
        public string[] Sums;

        public long End
        {
            get { long e = Offset; foreach (long s in Sizes) e += s; return e; }
        }

        // Распакованная карта больше этого — не карта, а что-то не то (и не повод занять всю память)
        const int MaxText = 16 * 1024 * 1024;

        // Карта в виде electron-builder: gzip, внутри JSON. На всякий случай понимает и сырой deflate, и просто JSON.
        // Что не разобрать — null, то есть «карты нет»: ошибкой для человека это не становится.
        public static BlockMap Parse(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            BlockMap m = FromJson(Unpack(data, true));
            if (m == null) m = FromJson(Unpack(data, false));
            if (m == null) m = FromJson(Plain(data));
            return m;
        }

        static string Unpack(byte[] data, bool gzip)
        {
            try
            {
                using (var input = new MemoryStream(data))
                using (Stream z = gzip ? (Stream)new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        if (output.Length > MaxText) return null;
                    }
                    return output.Length == 0 ? null : Plain(output.ToArray());
                }
            }
            catch { return null; }
        }

        static string Plain(byte[] data)
        {
            if (data.Length > MaxText) return null;
            string s = Encoding.UTF8.GetString(data);
            return s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
        }

        static BlockMap FromJson(string text)
        {
            if (string.IsNullOrEmpty(text) || text.TrimStart().Length == 0 || text.TrimStart()[0] != '{') return null;
            try
            {
                var d = Json.Parse(text);
                if (d == null) return null;
                // В карте один файл — сам установщик; electron-builder называет его "file"
                Dictionary<string, object> file = null;
                foreach (object o in Json.List(d, "files"))
                {
                    var f = o as Dictionary<string, object>;
                    if (f == null) continue;
                    if (file == null || Json.Str(f, "name") == "file") file = f;
                }
                if (file == null) return null;
                var sums = new List<string>();
                foreach (object o in Json.List(file, "checksums")) { var s = o as string; if (string.IsNullOrEmpty(s)) return null; sums.Add(s); }
                var sizes = new List<long>();
                foreach (object o in Json.List(file, "sizes")) { long n = Convert.ToInt64(o); if (n <= 0) return null; sizes.Add(n); }
                if (sums.Count == 0 || sums.Count != sizes.Count) return null;
                object off;
                long offset = file.TryGetValue("offset", out off) && off != null ? Convert.ToInt64(off) : 0;
                if (offset < 0) return null;
                return new BlockMap { Offset = offset, Sizes = sizes.ToArray(), Sums = sums.ToArray() };
            }
            catch { return null; }
        }
    }

    // Кусок нового файла: либо берётся из прежнего файла (Local, начиная с From), либо качается из сети
    class Piece
    {
        public long At, Length, From;
        public bool Local;
    }

    class PatchPlan
    {
        public List<Piece> Pieces = new List<Piece>();
        public long Download;    // сколько байт придётся скачать
        public int Requests;     // сколько запросов на это уйдёт
    }

    static class Patch
    {
        // Недостающие куски, между которыми лежит не больше Gap байт, качаются одним запросом: лишнее просто скачается,
        // зато вместо сотен мелких запросов — несколько крупных
        public const long Gap = 64 * 1024;
        public const int MaxRequests = 100;
        public const double MaxShare = 0.7;   // качать больше этой доли файла — проще и надёжнее скачать его целиком

        public static PatchPlan Make(BlockMap old, long oldSize, BlockMap neu, long newSize)
        {
            return Make(old, oldSize, neu, newSize, Gap, MaxRequests, MaxShare);
        }

        // План сборки нового файла по двум картам. null — частичная загрузка не нужна или не выйдет: качать целиком.
        public static PatchPlan Make(BlockMap old, long oldSize, BlockMap neu, long newSize, long gap, int maxRequests, double maxShare)
        {
            if (old == null || neu == null || newSize <= 0 || old.End > oldSize || neu.End > newSize) return null;
            // Где в прежнем файле лежит блок с такой суммой и длиной
            var where = new Dictionary<string, long>(StringComparer.Ordinal);
            long pos = old.Offset;
            for (int i = 0; i < old.Sizes.Length; i++)
            {
                string key = old.Sums[i] + "|" + old.Sizes[i];
                if (!where.ContainsKey(key)) where[key] = pos;
                pos += old.Sizes[i];
            }

            var raw = new List<Piece>();
            if (neu.Offset > 0) Add(raw, 0, neu.Offset, false, 0);   // начало файла до первого блока карта не описывает
            pos = neu.Offset;
            for (int i = 0; i < neu.Sizes.Length; i++)
            {
                long from;
                bool local = where.TryGetValue(neu.Sums[i] + "|" + neu.Sizes[i], out from);
                Add(raw, pos, neu.Sizes[i], local, from);
                pos += neu.Sizes[i];
            }
            if (pos < newSize) Add(raw, pos, newSize - pos, false, 0);   // и хвост после последнего

            // Близкие недостающие куски — в один запрос, вместе с тем, что между ними
            var plan = new PatchPlan();
            int lastRemote = -1;
            foreach (Piece p in raw)
            {
                if (!p.Local && lastRemote >= 0)
                {
                    Piece prev = plan.Pieces[lastRemote];
                    if (p.At - (prev.At + prev.Length) <= gap)
                    {
                        plan.Pieces.RemoveRange(lastRemote + 1, plan.Pieces.Count - lastRemote - 1);
                        prev.Length = p.At + p.Length - prev.At;
                        continue;
                    }
                }
                plan.Pieces.Add(new Piece { At = p.At, Length = p.Length, From = p.From, Local = p.Local });
                if (!p.Local) lastRemote = plan.Pieces.Count - 1;
            }
            foreach (Piece p in plan.Pieces)
                if (!p.Local) { plan.Download += p.Length; plan.Requests++; }
            if (plan.Requests > maxRequests || plan.Download > newSize * maxShare) return null;
            return plan;
        }

        // Соседние куски одного рода сливаются: подряд идущие недостающие — в один, подряд идущие из прежнего файла —
        // если и там они лежали подряд
        static void Add(List<Piece> list, long at, long length, bool local, long from)
        {
            if (list.Count > 0)
            {
                Piece last = list[list.Count - 1];
                if (last.Local == local && (!local || last.From + last.Length == from)) { last.Length += length; return; }
            }
            list.Add(new Piece { At = at, Length = length, Local = local, From = from });
        }

        // Собирает новый файл по плану: куски из прежнего файла копируются, остальные берутся у fetch(начало, длина) —
        // поток ровно этих байт. downloaded — сколько байт уже пришло из сети. Возвращает SHA-256 собранного (hex).
        // Поток короче или длиннее обещанного — исключение: такой файл ставить нельзя.
        public static string Assemble(PatchPlan plan, Stream old, Func<long, long, Stream> fetch, Stream output, Action<long> downloaded)
        {
            var buf = new byte[81920];
            long got = 0;
            using (var sha = SHA256.Create())
            {
                foreach (Piece p in plan.Pieces)
                {
                    if (p.Local)
                    {
                        old.Position = p.From;
                        Copy(old, p.Length, output, sha, buf, null);
                    }
                    else
                    {
                        using (Stream input = fetch(p.At, p.Length))
                        {
                            long before = got;
                            Copy(input, p.Length, output, sha, buf, delegate(long n) { got = before + n; if (downloaded != null) downloaded(got); });
                            if (input.Read(buf, 0, 1) > 0) throw new Exception("range is longer than asked");
                        }
                    }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        static void Copy(Stream input, long length, Stream output, HashAlgorithm sha, byte[] buf, Action<long> progress)
        {
            long done = 0;
            while (done < length)
            {
                int n = input.Read(buf, 0, (int)Math.Min(buf.Length, length - done));
                if (n <= 0) throw new Exception("range is shorter than asked");
                output.Write(buf, 0, n);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (progress != null) progress(done);
            }
        }
    }

    // Запас: последний скачанный файл каждого приложения с картой блоков — чтобы следующее обновление докачало только
    // изменившееся. Один файл и одна карта на приложение; лежит рядом с настройками core.txt, но в Local, а не в Roaming:
    // 150 МБ не должны ездить вместе с профилем Windows.
    static class Stash
    {
        public static string Root
        {
            get
            {
                if (State.DirOverride != null) return Path.Combine(State.DirOverride, "cache");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dev.txt", "core", "cache");
            }
        }

        static string Dir(string id)
        {
            if (string.IsNullOrEmpty(id) || id == "." || id == ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return Path.Combine(Root, id);
        }

        // Прежний файл и его карта, если они есть
        public static bool Find(string id, out string file, out byte[] map)
        {
            file = null; map = null;
            string dir = Dir(id);
            if (dir == null || !Directory.Exists(dir)) return false;
            try
            {
                foreach (string m in Directory.GetFiles(dir, "*.blockmap"))
                {
                    string f = m.Substring(0, m.Length - ".blockmap".Length);
                    if (!File.Exists(f)) continue;
                    file = f;
                    map = File.ReadAllBytes(m);
                    return true;
                }
            }
            catch { }
            file = null; map = null;
            return false;
        }

        // Кладёт скачанный файл в запас вместо прежнего — переносом, без второй копии. Возвращает, где файл теперь;
        // не вышло — прежнее место, файл остаётся во временной папке и ставится оттуда, как раньше.
        public static string Put(string id, string file, byte[] map)
        {
            string dir = Dir(id);
            if (dir == null || map == null) return file;
            string dest = Path.Combine(dir, Path.GetFileName(file)), destMap = dest + ".blockmap";
            try
            {
                Clear(id);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(destMap, map);
                File.Move(file, dest);
                return dest;
            }
            catch
            {
                if (!File.Exists(dest)) Releases.TryDelete(destMap);   // карта без файла не нужна
                return File.Exists(file) ? file : dest;
            }
        }

        // Файл лежит в запасе: после установки его не удаляют и не уносят
        public static bool Holds(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            try
            {
                string root = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
                return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Clear(string id)
        {
            string dir = Dir(id);
            try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}
