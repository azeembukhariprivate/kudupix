using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kudupix.Core;
using StbImageSharp;

namespace Kudupix.Tests
{
    public static partial class Program
    {
        private static FaceFinder _finder;
        private static FaceFinder Finder => _finder ?? (_finder = new FaceFinder(Samples.Models));

        /// <summary>A picture file as pixels (the app opens them with Windows' codecs; the tests with StbImageSharp).</summary>
        private static Pixels Open(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var img = ImageResult.FromStream(fs, ColorComponents.RedGreenBlueAlpha);
                var p = new Pixels(img.Width, img.Height);
                for (var i = 0; i < img.Width * img.Height; i++)
                {
                    p.Bgra[i * 4] = img.Data[i * 4 + 2];
                    p.Bgra[i * 4 + 1] = img.Data[i * 4 + 1];
                    p.Bgra[i * 4 + 2] = img.Data[i * 4];
                    p.Bgra[i * 4 + 3] = 255;
                }
                return p;
            }
        }

        private static Pixels Mirror(Pixels p)
        {
            var o = new Pixels(p.Width, p.Height);
            for (var y = 0; y < p.Height; y++)
                for (var x = 0; x < p.Width; x++)
                    Buffer.BlockCopy(p.Bgra, (y * p.Width + p.Width - 1 - x) * 4, o.Bgra, (y * p.Width + x) * 4, 4);
            return o;
        }

        private static Face Only(Pixels p, string what)
        {
            var faces = Finder.Find(p);
            Check(faces.Count == 1, $"one face in {what}, found {faces.Count}");
            return faces[0];
        }

        // ================================================================== finding faces

        private static void TestFindingFaces()
        {
            var lena = Only(Open(Samples.Lena), "Lena");
            // Her face is in the middle left of the picture: about 210, 182 to 352, 388 of 512 × 512.
            Near(210.0 / 512, lena.X, 0.03, "face left");
            Near(182.0 / 512, lena.Y, 0.03, "face top");
            Near(142.0 / 512, lena.W, 0.03, "face width");
            Check(lena.Score > 0.85, "sure it's a face");
            Equal(128, lena.Vector.Length, "128 numbers");
            Near(1, Math.Sqrt(lena.Vector.Sum(v => (double)v * v)), 1e-4, "made length 1");
            Only(Open(Samples.Messi), "the football photo");
            Only(Open(Samples.Audrey), "the portrait");
            var group = Finder.Find(Open(Samples.Class));
            Check(group.Count >= 30, "most of the faces in a class photo, found " + group.Count);
            Check(group.All(f => f.X >= 0 && f.Y >= 0 && f.X + f.W <= 1.02 && f.Y + f.H <= 1.02), "inside the photo");
            Check(Finder.Find(new Pixels(320, 240)).Count == 0, "no faces in an empty picture");
        }

        private static void TestSamePerson()
        {
            var lena = Open(Samples.Lena);
            var a = Only(lena, "Lena").Vector;
            var flipped = Only(Mirror(lena), "Lena mirrored").Vector;
            var smaller = Only(ImageOps.Straighten(ImageOps.Shrink(lena, 256), 6), "Lena smaller and turned").Vector;
            var darker = Only(new EditRecipe { Brightness = -40 }.Apply(lena), "Lena darker").Vector;
            Check(Face.Similarity(a, flipped) >= FaceGroups.Sure, "mirrored: the same person, " + Face.Similarity(a, flipped));
            Check(Face.Similarity(a, smaller) >= FaceGroups.Sure, "smaller and turned: the same person, " + Face.Similarity(a, smaller));
            Check(Face.Similarity(a, darker) >= FaceGroups.Sure, "darker: the same person, " + Face.Similarity(a, darker));
            var messi = Only(Open(Samples.Messi), "football").Vector;
            var audrey = Only(Open(Samples.Audrey), "portrait").Vector;
            Check(Face.Similarity(a, messi) < FaceGroups.Maybe, "different people: " + Face.Similarity(a, messi));
            Check(Face.Similarity(a, audrey) < FaceGroups.Maybe, "different people: " + Face.Similarity(a, audrey));
            Check(Face.Similarity(messi, audrey) < FaceGroups.Maybe, "different people: " + Face.Similarity(messi, audrey));
            // A class photo: many small faces. Different children are never put together.
            var group = Finder.Find(Open(Samples.Class));
            var clear = group.Where(f => f.Clear).ToList();
            for (var i = 0; i < clear.Count; i++)
                for (var j = i + 1; j < clear.Count; j++)
                    Check(Face.Similarity(clear[i].Vector, clear[j].Vector) < FaceGroups.Sure, "two different children's clear faces: " + Face.Similarity(clear[i].Vector, clear[j].Vector));
            var g = FaceGroups.Work(group.Select((f, i) => ("class.png|" + i, f)).ToList(), new List<Person>(), new HashSet<string>());
            Equal(0, g.Unnamed.Count, "no group mixing children");
            Check(group.Any(f => !f.Clear), "small faces are found (to name by hand)");
        }

        private static void TestLiningUpFaces()
        {
            // Points turned 30°, made twice as big and moved: the best fit finds exactly that.
            var from = new double[] { 10, 10, 30, 12, 20, 25, 12, 35, 28, 36 };
            var angle = 30 * Math.PI / 180;
            double A = 2 * Math.Cos(angle), B = 2 * Math.Sin(angle);
            var to = new double[10];
            for (var i = 0; i < 5; i++)
            {
                to[i * 2] = A * from[i * 2] - B * from[i * 2 + 1] + 5;
                to[i * 2 + 1] = B * from[i * 2] + A * from[i * 2 + 1] - 7;
            }
            var (a, b, tx, ty) = FaceFinder.SimilarityTo(from, to);
            Near(A, a, 1e-9, "turn and size (a)");
            Near(B, b, 1e-9, "turn and size (b)");
            Near(5, tx, 1e-9, "moved across");
            Near(-7, ty, 1e-9, "moved down");
            var face = Only(Open(Samples.Lena), "Lena");
            var lined = FaceFinder.Align(Open(Samples.Lena), Finder.Detect(Open(Samples.Lena))[0].Landmarks);
            Equal(112, lined.Width, "112 wide");
            Equal(112, lined.Height, "112 high");
        }

        // ================================================================== keeping faces and people

        private static void TestFaceStore()
        {
            var file = Path.Combine(Folder("faces"), "faces.bin");
            var store = new FaceStore(file);
            var item = new MediaItem { Path = @"C:\Pictures\a.jpg", Size = 10, Modified = 5 };
            Check(!store.Done(item), "not looked at yet");
            store.Put(new PhotoFaces { Path = item.Path, Size = 10, Modified = 5, Faces = { new Face { X = 0.1, Y = 0.2, W = 0.3, H = 0.4, Score = 0.9f, Vector = new[] { 0.6f, 0.8f } } } });
            store.Put(new PhotoFaces { Path = @"C:\Pictures\none.jpg", Size = 1, Modified = 1 });
            Check(store.Done(item), "looked at");
            Check(!store.Done(new MediaItem { Path = item.Path, Size = 11, Modified = 5 }), "a changed photo is looked at again");
            store.Save();
            var again = new FaceStore(file);
            Check(again.Load(), "loaded");
            Equal(2, again.PhotoCount, "both photos (one with no faces)");
            var f = again.Face(FaceStore.Id(item.Path, 0));
            Check(f != null && f.X == 0.1 && f.Vector[1] == 0.8f, "the face kept");
            Check(again.Face(FaceStore.Id(item.Path, 1)) == null, "no second face");
            again.Move(item.Path, @"C:\Pictures\b.jpg");
            Check(again.Face(FaceStore.Id(@"C:\Pictures\b.jpg", 0)) != null, "moved");
            again.Keep(new[] { @"C:\Pictures\b.jpg" });
            Equal(1, again.PhotoCount, "photos no longer there are dropped");
            Equal((@"C:\Pictures\x|y.jpg", 3), FaceStore.Split(FaceStore.Id(@"C:\Pictures\x|y.jpg", 3)), "ids split back");
        }

        private static void TestPeopleData()
        {
            var file = Path.Combine(Folder("people"), "people.json");
            var data = new PeopleData(file);
            var mum = data.Name(new[] { "a.jpg|0", "b.jpg|0" }, " Mum ");
            Equal("Mum", mum.Name, "named");
            Check(data.Name(new[] { "c.jpg|1" }, "mum") == mum, "the same name joins the same person");
            Equal(3, mum.Faces.Count, "three faces");
            var dad = data.Name(new[] { "b.jpg|0" }, "Dad");
            Check(!mum.Faces.Contains("b.jpg|0") && dad.Faces.Contains("b.jpg|0"), "a face is one person");
            data.Reject(mum, "a.jpg|0");
            Check(!mum.Faces.Contains("a.jpg|0") && mum.Rejected.Contains("a.jpg|0"), "not this person");
            data.NotAFace("c.jpg|1");
            Check(data.NotFaces().Contains("c.jpg|1") && !mum.Faces.Contains("c.jpg|1"), "not a face");
            data.Confirm(mum, new[] { "c.jpg|1" });
            Check(!data.NotFaces().Contains("c.jpg|1"), "a face again when someone's named");
            data.Hide(dad, true);
            data.Move("c.jpg", "d.jpg");
            Check(mum.Faces.Contains("d.jpg|1"), "ids follow a moved photo");
            data.Save();
            data.Save();
            var loaded = new PeopleData(file);
            Check(loaded.Load(), "loaded");
            Equal(2, loaded.People().Count, "two people");
            Check(loaded.FindByName("dad").Hidden, "hidden kept");
            Check(loaded.FindByName("MUM").Rejected.Contains("a.jpg|0"), "\"not this person\" kept");
            File.WriteAllText(file, "{ damaged");
            Check(new PeopleData(file).Load(), "the spare copy is read");
            var l2 = loaded.FindByName("mum");
            loaded.Rename(loaded.FindByName("dad"), "Mum");
            Equal(1, loaded.People().Count, "renaming to someone already there joins them");
            Check(l2.Faces.Contains("b.jpg|0"), "with the faces");
            loaded.Forget(new[] { "d.jpg" });
            Check(!l2.Faces.Contains("d.jpg|1"), "a photo gone: its faces go");
        }

        private static void TestGroupingFaces()
        {
            var lena = Open(Samples.Lena);
            var faces = new List<(string, Face)>
            {
                ("lena.jpg|0", Only(lena, "Lena")),
                ("mirror.jpg|0", Only(Mirror(lena), "mirrored")),
                ("small.jpg|0", Only(ImageOps.Shrink(lena, 300), "smaller")),
                ("messi.jpg|0", Only(Open(Samples.Messi), "football")),
                ("audrey.jpg|0", Only(Open(Samples.Audrey), "portrait"))
            };
            var people = new PeopleData(Path.Combine(Folder("grouping"), "people.json"));
            var g = FaceGroups.Work(faces, people.People(), people.NotFaces());
            Equal(1, g.Unnamed.Count, "one group: the three photos of Lena");
            Check(g.Unnamed[0].Count == 3 && g.Unnamed[0].All(id => id != "messi.jpg|0" && id != "audrey.jpg|0"), "just her");
            // The football photo's face is small (30 pixels): found, but not used to group people.
            Check(!faces[3].Item2.Clear && faces[0].Item2.Clear, "small and clear faces");
            Equal(faces.Skip(3).Count(f => f.Item2.Clear), g.Alone.Count, "the others (clear enough) seen once");
            Check(!g.Alone.Contains("messi.jpg|0"), "the small face isn't in any group");

            // Named from one photo: the others join her by themselves.
            var person = people.Name(new[] { "lena.jpg|0" }, "Lena");
            g = FaceGroups.Work(faces, people.People(), people.NotFaces());
            Check(g.FacesOf(person).Count == 3, "her other photos join her");
            Equal(0, g.Unnamed.Count, "no unnamed group left");

            // A face only somewhat like her is asked about, not added.
            var a = faces[0].Item2.Vector;
            var other = faces[3].Item2.Vector;
            var orth = a.Zip(other, (x, y) => y - Face.Similarity(a, other) * x).ToArray();
            var length = (float)Math.Sqrt(orth.Sum(x => x * x));
            var maybe = a.Zip(orth, (x, y) => 0.42f * x + (float)Math.Sqrt(1 - 0.42 * 0.42) * y / length).ToArray();
            faces.Add(("maybe.jpg|0", new Face { Vector = maybe, Score = 0.9f, Side = 120 }));
            g = FaceGroups.Work(faces, people.People(), people.NotFaces());
            Check(!g.PersonOf.ContainsKey("maybe.jpg|0"), "not added");
            Check(g.Ask.ContainsKey(person) && g.Ask[person].Any(x => x.Id == "maybe.jpg|0"), "asked about");
            people.Confirm(person, new[] { "maybe.jpg|0" });
            g = FaceGroups.Work(faces, people.People(), people.NotFaces());
            Check(g.PersonOf["maybe.jpg|0"] == person && !g.Ask.ContainsKey(person), "yes: hers");

            // "Not Lena" and "not a face" are kept.
            people.Reject(person, "small.jpg|0");
            people.NotAFace("mirror.jpg|0");
            g = FaceGroups.Work(faces, people.People(), people.NotFaces());
            Check(!g.PersonOf.ContainsKey("small.jpg|0"), "not given to her again");
            Check(!g.PersonOf.ContainsKey("mirror.jpg|0") && !g.Alone.Contains("mirror.jpg|0") && g.Unnamed.All(x => !x.Contains("mirror.jpg|0")), "not a face: left out");
        }
    }
}
