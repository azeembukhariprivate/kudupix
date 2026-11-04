using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kudupix.Core
{
    /// <summary>A face found in a photo: where (parts of the photo's width and height, as shown) and what it looks like.</summary>
    public sealed class Face
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public float Score { get; set; }
        /// <summary>The face's shorter side in pixels, in the picture it was found in (small faces say little about who it is).</summary>
        public float Side { get; set; }
        /// <summary>Big enough to tell who it is: only these join people or groups by themselves.</summary>
        public bool Clear => Side >= MinClearSide;
        public const float MinClearSide = 40;
        /// <summary>128 numbers describing the face (length 1); two faces of the same person point the same way.</summary>
        public float[] Vector { get; set; }
        /// <summary>The eyes, nose and mouth corners in pixels of the picture looked at (used to line the face up).</summary>
        public double[] Landmarks { get; set; }

        public static float Similarity(float[] a, float[] b)
        {
            double s = 0;
            for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
            return (float)s;
        }
    }

    /// <summary>
    /// Finds faces in a picture and describes each one, on this PC, with two small models from the OpenCV project:
    /// YuNet (finds faces and five points on each) and SFace (128 numbers per face, lined up by those points).
    /// </summary>
    public sealed class FaceFinder : IDisposable
    {
        public const string DetectorFile = "face_detection_yunet_2023mar.onnx";
        public const string DescriberFile = "face_recognition_sface_2021dec.onnx";
        private const int Size = 640;
        private readonly InferenceSession _detector;
        private readonly InferenceSession _describer;
        private readonly object _sync = new object();

        /// <summary>Faces less sure than this aren't kept (OpenCV's own default is 0.9).</summary>
        public float MinScore { get; set; } = 0.85f;

        /// <summary>Faces smaller than this (in pixels of the picture looked at) are too small to tell who it is.</summary>
        public int MinSide { get; set; } = 24;

        public FaceFinder(string folder)
        {
            var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2), LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
            _detector = new InferenceSession(Path.Combine(folder, DetectorFile), options);
            _describer = new InferenceSession(Path.Combine(folder, DescriberFile), options);
        }

        public static bool ModelsIn(string folder) =>
            File.Exists(Path.Combine(folder, DetectorFile)) && File.Exists(Path.Combine(folder, DescriberFile));

        /// <summary>The faces in a picture, each described; positions are parts of the picture's width and height.</summary>
        public List<Face> Find(Pixels p)
        {
            var faces = Detect(p);
            foreach (var f in faces) f.Vector = Describe(p, f.Landmarks);
            return faces;
        }

        /// <summary>Where the faces are (not yet described).</summary>
        public List<Face> Detect(Pixels p)
        {
            // The picture made to fit 640 × 640, the rest left black.
            var scale = Math.Min((double)Size / p.Width, (double)Size / p.Height);
            var w = Math.Max(1, (int)Math.Round(p.Width * scale));
            var h = Math.Max(1, (int)Math.Round(p.Height * scale));
            var small = scale < 1 ? ImageOps.Resize(p, w, h) : Enlarge(p, w, h);
            var input = new DenseTensor<float>(new[] { 1, 3, Size, Size });
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    input[0, 0, y, x] = small.Bgra[i];     // blue, green, red: as the model learned
                    input[0, 1, y, x] = small.Bgra[i + 1];
                    input[0, 2, y, x] = small.Bgra[i + 2];
                }
            Dictionary<string, float[]> outputs;
            lock (_sync)
                using (var results = _detector.Run(new[] { NamedOnnxValue.CreateFromTensor("input", input) }))
                    outputs = results.ToDictionary(r => r.Name, r => r.AsEnumerable<float>().ToArray());

            var found = new List<Face>();
            foreach (var stride in new[] { 8, 16, 32 })
            {
                var cols = Size / stride;
                var cls = outputs["cls_" + stride];
                var obj = outputs["obj_" + stride];
                var box = outputs["bbox_" + stride];
                var kps = outputs["kps_" + stride];
                for (var i = 0; i < cls.Length; i++)
                {
                    var score = (float)Math.Sqrt(Clamp01(cls[i]) * Clamp01(obj[i]));
                    if (score < MinScore) continue;
                    var r = i / cols;
                    var c = i % cols;
                    var cx = (c + box[i * 4]) * stride;
                    var cy = (r + box[i * 4 + 1]) * stride;
                    var bw = Math.Exp(box[i * 4 + 2]) * stride;
                    var bh = Math.Exp(box[i * 4 + 3]) * stride;
                    var marks = new double[10];
                    for (var n = 0; n < 5; n++)
                    {
                        marks[n * 2] = (kps[i * 10 + n * 2] + c) * stride / scale;
                        marks[n * 2 + 1] = (kps[i * 10 + n * 2 + 1] + r) * stride / scale;
                    }
                    found.Add(new Face
                    {
                        X = (cx - bw / 2) / scale / p.Width,
                        Y = (cy - bh / 2) / scale / p.Height,
                        W = bw / scale / p.Width,
                        H = bh / scale / p.Height,
                        Score = score,
                        Side = (float)(Math.Min(bw, bh) / scale),
                        Landmarks = marks
                    });
                }
            }
            // One box per face: the surest, and none overlapping it by more than 30%.
            var keep = new List<Face>();
            foreach (var f in found.OrderByDescending(f => f.Score))
                if (keep.All(k => Overlap(k, f, p) <= 0.3)) keep.Add(f);
            return keep.Where(f => Math.Min(f.W * p.Width, f.H * p.Height) >= MinSide)
                       .OrderBy(f => f.X).ThenBy(f => f.Y).ToList();
        }

        private static double Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static double Overlap(Face a, Face b, Pixels p)
        {
            var x1 = Math.Max(a.X, b.X);
            var y1 = Math.Max(a.Y, b.Y);
            var x2 = Math.Min(a.X + a.W, b.X + b.W);
            var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
            var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            var union = a.W * a.H + b.W * b.H - inter;
            return union <= 0 ? 0 : inter / union;
        }

        /// <summary>Makes a small picture bigger (nearest pixel; YuNet only needs the rough shape).</summary>
        private static Pixels Enlarge(Pixels p, int w, int h)
        {
            var o = new Pixels(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    Buffer.BlockCopy(p.Bgra, ((y * p.Height / h) * p.Width + x * p.Width / w) * 4, o.Bgra, (y * w + x) * 4, 4);
            return o;
        }

        /// <summary>Where the five points go on a lined-up 112 × 112 face (as SFace learned them).</summary>
        private static readonly double[] Target = { 38.2946, 51.6963, 73.5318, 51.5014, 56.0252, 71.7366, 41.5493, 92.3655, 70.7299, 92.2041 };

        /// <summary>The face lined up (turned and sized so the eyes, nose and mouth are where SFace expects them), 112 × 112.</summary>
        public static Pixels Align(Pixels p, double[] marks)
        {
            var (a, b, tx, ty) = SimilarityTo(marks, Target);
            // The way back: from the lined-up face to the picture.
            var det = a * a + b * b;
            var ia = a / det;
            var ib = -b / det;
            var o = new Pixels(112, 112);
            for (var y = 0; y < 112; y++)
                for (var x = 0; x < 112; x++)
                {
                    var dx = x - tx;
                    var dy = y - ty;
                    var sx = ia * dx - ib * dy;
                    var sy = ib * dx + ia * dy;
                    Bilinear(p, sx, sy, o.Bgra, (y * 112 + x) * 4);
                }
            return o;
        }

        /// <summary>
        /// The turn, size and move (no mirroring) that best takes the points <paramref name="from"/> onto <paramref name="to"/>:
        /// x' = a·x − b·y + tx, y' = b·x + a·y + ty.
        /// </summary>
        public static (double A, double B, double Tx, double Ty) SimilarityTo(double[] from, double[] to)
        {
            var n = from.Length / 2;
            double fx = 0, fy = 0, tx = 0, ty = 0;
            for (var i = 0; i < n; i++)
            {
                fx += from[i * 2]; fy += from[i * 2 + 1];
                tx += to[i * 2]; ty += to[i * 2 + 1];
            }
            fx /= n; fy /= n; tx /= n; ty /= n;
            double num1 = 0, num2 = 0, den = 0;
            for (var i = 0; i < n; i++)
            {
                var x = from[i * 2] - fx;
                var y = from[i * 2 + 1] - fy;
                var u = to[i * 2] - tx;
                var v = to[i * 2 + 1] - ty;
                num1 += x * u + y * v;
                num2 += x * v - y * u;
                den += x * x + y * y;
            }
            var a = den == 0 ? 1 : num1 / den;
            var b = den == 0 ? 0 : num2 / den;
            return (a, b, tx - (a * fx - b * fy), ty - (b * fx + a * fy));
        }

        private static void Bilinear(Pixels p, double x, double y, byte[] dst, int at)
        {
            if (x < -1 || y < -1 || x > p.Width || y > p.Height) { dst[at + 3] = 255; return; }
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var fx = x - x0;
            var fy = y - y0;
            for (var c = 0; c < 3; c++)
            {
                double P(int xx, int yy)
                {
                    xx = Math.Max(0, Math.Min(p.Width - 1, xx));
                    yy = Math.Max(0, Math.Min(p.Height - 1, yy));
                    return p.Bgra[(yy * p.Width + xx) * 4 + c];
                }
                var v = (P(x0, y0) * (1 - fx) + P(x0 + 1, y0) * fx) * (1 - fy) + (P(x0, y0 + 1) * (1 - fx) + P(x0 + 1, y0 + 1) * fx) * fy;
                dst[at + c] = (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
            }
            dst[at + 3] = 255;
        }

        /// <summary>The 128 numbers for a face (made length 1, so comparing two is one multiplication each).</summary>
        public float[] Describe(Pixels p, double[] marks)
        {
            var face = Align(p, marks);
            var input = new DenseTensor<float>(new[] { 1, 3, 112, 112 });
            for (var y = 0; y < 112; y++)
                for (var x = 0; x < 112; x++)
                {
                    var i = (y * 112 + x) * 4;
                    input[0, 0, y, x] = face.Bgra[i + 2]; // red, green, blue
                    input[0, 1, y, x] = face.Bgra[i + 1];
                    input[0, 2, y, x] = face.Bgra[i];
                }
            float[] v;
            lock (_sync)
                using (var results = _describer.Run(new[] { NamedOnnxValue.CreateFromTensor("data", input) }))
                    v = results.First().AsEnumerable<float>().ToArray();
            var length = Math.Sqrt(v.Sum(x => (double)x * x));
            if (length > 0) for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / length);
            return v;
        }

        public void Dispose()
        {
            _detector.Dispose();
            _describer.Dispose();
        }
    }

    /// <summary>The faces found in one photo (an empty list means looked at, no faces).</summary>
    public sealed class PhotoFaces
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public long Modified { get; set; }
        public List<Face> Faces { get; set; } = new List<Face>();
    }

    /// <summary>
    /// Every photo's faces, kept in one file (faces.bin) so faces are only looked for once per photo (again when it
    /// changes). Only the positions and the 128 numbers are kept, never a picture of the face.
    /// </summary>
    public sealed class FaceStore
    {
        private const string Magic = "KUDUPIX-FACES-1";
        private readonly object _sync = new object();
        private readonly Dictionary<string, PhotoFaces> _photos = new Dictionary<string, PhotoFaces>(StringComparer.OrdinalIgnoreCase);

        public string FilePath { get; }

        public FaceStore(string filePath)
        {
            FilePath = filePath;
        }

        public static string Id(string path, int index) => path + "|" + index;

        public static (string Path, int Index) Split(string id)
        {
            var bar = id.LastIndexOf('|');
            return bar < 0 ? (id, 0) : (id.Substring(0, bar), int.TryParse(id.Substring(bar + 1), out var n) ? n : 0);
        }

        /// <summary>Whether this photo, as it is now, has been looked at.</summary>
        public bool Done(MediaItem item)
        {
            lock (_sync) return _photos.TryGetValue(item.Path, out var p) && p.Size == item.Size && p.Modified == item.Modified;
        }

        public void Put(PhotoFaces photo)
        {
            lock (_sync) _photos[photo.Path] = photo;
        }

        public PhotoFaces Get(string path)
        {
            lock (_sync) return _photos.TryGetValue(path, out var p) ? p : null;
        }

        public Face Face(string id)
        {
            var (path, index) = Split(id);
            var p = Get(path);
            return p != null && index >= 0 && index < p.Faces.Count ? p.Faces[index] : null;
        }

        /// <summary>Every face, with its id.</summary>
        public List<(string Id, Face Face)> All()
        {
            lock (_sync) return _photos.Values.SelectMany(p => p.Faces.Select((f, i) => (Id(p.Path, i), f))).ToList();
        }

        public int PhotoCount
        {
            get { lock (_sync) return _photos.Count; }
        }

        /// <summary>Drops photos no longer in the library.</summary>
        public void Keep(ICollection<string> paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            lock (_sync)
                foreach (var gone in _photos.Keys.Where(k => !set.Contains(k)).ToList()) _photos.Remove(gone);
        }

        public void Move(string from, string to)
        {
            lock (_sync)
            {
                if (!_photos.TryGetValue(from, out var p)) return;
                _photos.Remove(from);
                p.Path = to;
                _photos[to] = p;
            }
        }

        public void Save()
        {
            List<PhotoFaces> photos;
            lock (_sync) photos = _photos.Values.ToList();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath) ?? ".");
            var temp = FilePath + ".new";
            using (var w = new BinaryWriter(new FileStream(temp, FileMode.Create, FileAccess.Write), Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(photos.Count);
                foreach (var p in photos)
                {
                    w.Write(p.Path);
                    w.Write(p.Size);
                    w.Write(p.Modified);
                    w.Write(p.Faces.Count);
                    foreach (var f in p.Faces)
                    {
                        w.Write(f.X); w.Write(f.Y); w.Write(f.W); w.Write(f.H);
                        w.Write(f.Score);
                        w.Write(f.Side);
                        w.Write(f.Vector?.Length ?? 0);
                        foreach (var v in f.Vector ?? new float[0]) w.Write(v);
                    }
                }
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }

        public bool Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var photos = new List<PhotoFaces>();
                using (var r = new BinaryReader(new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read), Encoding.UTF8))
                {
                    if (r.ReadString() != Magic) return false;
                    var count = r.ReadInt32();
                    for (var n = 0; n < count; n++)
                    {
                        var p = new PhotoFaces { Path = r.ReadString(), Size = r.ReadInt64(), Modified = r.ReadInt64() };
                        var faces = r.ReadInt32();
                        for (var i = 0; i < faces; i++)
                        {
                            var f = new Face { X = r.ReadDouble(), Y = r.ReadDouble(), W = r.ReadDouble(), H = r.ReadDouble(), Score = r.ReadSingle(), Side = r.ReadSingle() };
                            var length = r.ReadInt32();
                            f.Vector = new float[length];
                            for (var k = 0; k < length; k++) f.Vector[k] = r.ReadSingle();
                            p.Faces.Add(f);
                        }
                        photos.Add(p);
                    }
                }
                lock (_sync)
                {
                    _photos.Clear();
                    foreach (var p in photos) _photos[p.Path] = p;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is EndOfStreamException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException)
            {
                return false;
            }
        }
    }
}
