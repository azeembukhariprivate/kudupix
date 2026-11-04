using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Kudupix.Core
{
    /// <summary>A named person: the faces said to be them, and the ones said not to be.</summary>
    [DataContract]
    public sealed class Person
    {
        [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember] public string Name { get; set; } = string.Empty;
        /// <summary>Not listed (strangers, people you don't want to see).</summary>
        [DataMember] public bool Hidden { get; set; }
        /// <summary>Faces you said are this person.</summary>
        [DataMember] public List<string> Faces { get; set; } = new List<string>();
        /// <summary>Faces you said aren't this person.</summary>
        [DataMember] public List<string> Rejected { get; set; } = new List<string>();

        public override string ToString() => Name;
    }

    [DataContract]
    internal sealed class PeopleFile
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public List<Person> People { get; set; } = new List<Person>();
        [DataMember] public List<string> NotFaces { get; set; } = new List<string>();
    }

    /// <summary>The people you've named, and what you said about faces (people.json). Never written into the photos.</summary>
    public sealed class PeopleData
    {
        private readonly object _sync = new object();
        private readonly List<Person> _people = new List<Person>();
        private readonly HashSet<string> _notFaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string FilePath { get; }
        public event Action Changed;

        public PeopleData(string filePath)
        {
            FilePath = filePath;
        }

        public List<Person> People()
        {
            lock (_sync) return _people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public Person Find(string id)
        {
            lock (_sync) return _people.FirstOrDefault(p => p.Id == id);
        }

        public Person FindByName(string name)
        {
            lock (_sync) return _people.FirstOrDefault(p => string.Equals(p.Name, (name ?? string.Empty).Trim(), StringComparison.CurrentCultureIgnoreCase));
        }

        public HashSet<string> NotFaces()
        {
            lock (_sync) return new HashSet<string>(_notFaces, StringComparer.OrdinalIgnoreCase);
        }

        private void Raise() => Changed?.Invoke();

        /// <summary>
        /// Says these faces are the person with this name: a new person, or one already named that (the faces join
        /// them). Returns the person.
        /// </summary>
        public Person Name(IEnumerable<string> faceIds, string name)
        {
            name = UserData.CleanTag(name);
            if (name.Length == 0) return null;
            Person person;
            lock (_sync)
            {
                person = _people.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
                if (person == null)
                {
                    person = new Person { Name = name };
                    _people.Add(person);
                }
                ConfirmLocked(person, faceIds);
            }
            Raise();
            return person;
        }

        /// <summary>"Yes, this is them."</summary>
        public void Confirm(Person person, IEnumerable<string> faceIds)
        {
            lock (_sync) ConfirmLocked(person, faceIds);
            Raise();
        }

        private void ConfirmLocked(Person person, IEnumerable<string> faceIds)
        {
            foreach (var id in faceIds)
            {
                // A face is one person: it leaves anyone else it was given to.
                foreach (var other in _people.Where(p => p != person)) other.Faces.RemoveAll(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase));
                person.Rejected.RemoveAll(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase));
                _notFaces.Remove(id);
                if (!person.Faces.Contains(id, StringComparer.OrdinalIgnoreCase)) person.Faces.Add(id);
            }
        }

        /// <summary>"No, this isn't them": the face won't be given to them again.</summary>
        public void Reject(Person person, string faceId)
        {
            lock (_sync)
            {
                person.Faces.RemoveAll(f => string.Equals(f, faceId, StringComparison.OrdinalIgnoreCase));
                if (!person.Rejected.Contains(faceId, StringComparer.OrdinalIgnoreCase)) person.Rejected.Add(faceId);
            }
            Raise();
        }

        /// <summary>Not a face at all (a pattern, a statue...): it's left out everywhere.</summary>
        public void NotAFace(string faceId)
        {
            lock (_sync)
            {
                _notFaces.Add(faceId);
                foreach (var p in _people) p.Faces.RemoveAll(f => string.Equals(f, faceId, StringComparison.OrdinalIgnoreCase));
            }
            Raise();
        }

        public void Rename(Person person, string name)
        {
            name = UserData.CleanTag(name);
            if (name.Length == 0) return;
            var same = FindByName(name);
            if (same != null && same != person)
            {
                // Renaming to someone already there: they're the same person.
                Merge(person, same);
                return;
            }
            lock (_sync) person.Name = name;
            Raise();
        }

        public void Hide(Person person, bool hidden)
        {
            lock (_sync) person.Hidden = hidden;
            Raise();
        }

        /// <summary>Two people who are the same: <paramref name="from"/>'s faces join <paramref name="into"/>.</summary>
        public void Merge(Person from, Person into)
        {
            if (from == into) return;
            lock (_sync)
            {
                foreach (var f in from.Faces) if (!into.Faces.Contains(f, StringComparer.OrdinalIgnoreCase)) into.Faces.Add(f);
                foreach (var f in from.Rejected) if (!into.Rejected.Contains(f, StringComparer.OrdinalIgnoreCase) && !into.Faces.Contains(f, StringComparer.OrdinalIgnoreCase)) into.Rejected.Add(f);
                _people.Remove(from);
            }
            Raise();
        }

        /// <summary>Forgets a person (their photos stay; their faces become unnamed again).</summary>
        public void Remove(Person person)
        {
            lock (_sync) _people.Remove(person);
            Raise();
        }

        /// <summary>A photo moved: its faces' ids follow it.</summary>
        public void Move(string fromPath, string toPath)
        {
            string Fix(string id)
            {
                var (path, index) = FaceStore.Split(id);
                return string.Equals(path, fromPath, StringComparison.OrdinalIgnoreCase) ? FaceStore.Id(toPath, index) : id;
            }
            lock (_sync)
            {
                foreach (var p in _people)
                {
                    p.Faces = p.Faces.Select(Fix).ToList();
                    p.Rejected = p.Rejected.Select(Fix).ToList();
                }
                var moved = _notFaces.Select(Fix).ToList();
                _notFaces.Clear();
                foreach (var m in moved) _notFaces.Add(m);
            }
            Raise();
        }

        /// <summary>A photo changed or went: what was said about its faces no longer applies.</summary>
        public void Forget(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            bool Gone(string id) => set.Contains(FaceStore.Split(id).Path);
            lock (_sync)
            {
                foreach (var p in _people)
                {
                    p.Faces.RemoveAll(Gone);
                    p.Rejected.RemoveAll(Gone);
                }
                _notFaces.RemoveWhere(Gone);
            }
            Raise();
        }

        public void Save()
        {
            var data = new PeopleFile();
            lock (_sync)
            {
                data.People = _people.ToList();
                data.NotFaces = _notFaces.ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
            var temp = FilePath + ".new";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                new DataContractJsonSerializer(typeof(PeopleFile)).WriteObject(fs, data);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }

        public bool Load()
        {
            foreach (var path in new[] { FilePath, FilePath + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    PeopleFile data;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        data = (PeopleFile)new DataContractJsonSerializer(typeof(PeopleFile)).ReadObject(fs);
                    lock (_sync)
                    {
                        _people.Clear();
                        foreach (var p in data.People ?? new List<Person>())
                        {
                            p.Faces = p.Faces ?? new List<string>();
                            p.Rejected = p.Rejected ?? new List<string>();
                            _people.Add(p);
                        }
                        _notFaces.Clear();
                        foreach (var n in data.NotFaces ?? new List<string>()) _notFaces.Add(n);
                    }
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException || ex is InvalidCastException)
                {
                    // Try the spare copy.
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Who each face is: faces you named, faces sure enough to join someone by themselves, faces to ask about
    /// ("Is this Mum?"), and groups of faces not named yet ("Who is this?"), biggest first. Faces too small to tell who
    /// it is (<see cref="Face.Clear"/>) are only ever named by hand.
    /// </summary>
    public sealed class FaceGroups
    {
        /// <summary>From this similarity a face joins a person by itself.</summary>
        public const float Sure = 0.50f;
        /// <summary>From this similarity (OpenCV's own "same person" line is 0.363) KUDUPIX asks.</summary>
        public const float Maybe = 0.37f;
        /// <summary>Faces this alike are put in one "Who is this?" group.</summary>
        public const float Together = 0.50f;

        /// <summary>The person each face is (named, or joined by itself).</summary>
        public Dictionary<string, Person> PersonOf { get; } = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Faces to ask about, per person, most alike first.</summary>
        public Dictionary<Person, List<(string Id, float Score)>> Ask { get; } = new Dictionary<Person, List<(string, float)>>();
        /// <summary>Unnamed faces in groups of the same person (at least two), biggest first.</summary>
        public List<List<string>> Unnamed { get; } = new List<List<string>>();
        /// <summary>Unnamed faces seen only once.</summary>
        public List<string> Alone { get; } = new List<string>();

        public List<string> FacesOf(Person p) => PersonOf.Where(x => x.Value == p).Select(x => x.Key).ToList();

        public static FaceGroups Work(IList<(string Id, Face Face)> faces, IList<Person> people, ISet<string> notFaces)
        {
            var g = new FaceGroups();
            var byId = faces.Where(f => f.Face.Vector != null && f.Face.Vector.Length > 0).ToDictionary(f => f.Id, f => f.Face, StringComparer.OrdinalIgnoreCase);
            var known = people.ToDictionary(p => p, p => p.Faces.Where(byId.ContainsKey).Select(id => byId[id].Vector).ToList());
            var rejected = people.ToDictionary(p => p, p => new HashSet<string>(p.Rejected, StringComparer.OrdinalIgnoreCase));
            foreach (var p in people)
                foreach (var id in p.Faces.Where(byId.ContainsKey))
                    g.PersonOf[id] = p;

            var left = new List<string>();
            foreach (var (id, face) in byId.Select(x => (x.Key, x.Value)).OrderByDescending(x => x.Item2.Score).ThenBy(x => x.Item1, StringComparer.Ordinal))
            {
                if (g.PersonOf.ContainsKey(id) || notFaces.Contains(id)) continue;
                // Too small to tell who it is: only named by hand.
                if (!face.Clear) continue;
                Person best = null;
                float bestScore = 0;
                foreach (var p in people)
                {
                    if (rejected[p].Contains(id) || known[p].Count == 0) continue;
                    var s = known[p].Max(v => Face.Similarity(v, face.Vector));
                    if (s > bestScore) { bestScore = s; best = p; }
                }
                if (best != null && bestScore >= Sure) g.PersonOf[id] = best;
                else if (best != null && bestScore >= Maybe)
                {
                    if (!g.Ask.TryGetValue(best, out var list)) g.Ask[best] = list = new List<(string, float)>();
                    list.Add((id, bestScore));
                }
                else left.Add(id);
            }
            foreach (var list in g.Ask.Values) list.Sort((a, b) => b.Score.CompareTo(a.Score));

            // The rest, in groups: each face joins the group it's most like (by the group's average face) if alike enough.
            var groups = new List<(List<string> Ids, float[] Sum)>();
            foreach (var id in left)
            {
                var v = byId[id].Vector;
                var bestGroup = -1;
                float bestScore = Together;
                for (var i = 0; i < groups.Count; i++)
                {
                    var s = Face.Similarity(Normal(groups[i].Sum), v);
                    if (s >= bestScore) { bestScore = s; bestGroup = i; }
                }
                if (bestGroup < 0) groups.Add((new List<string> { id }, (float[])v.Clone()));
                else
                {
                    groups[bestGroup].Ids.Add(id);
                    var sum = groups[bestGroup].Sum;
                    for (var k = 0; k < sum.Length; k++) sum[k] += v[k];
                }
            }
            foreach (var group in groups.OrderByDescending(x => x.Ids.Count).ThenBy(x => x.Ids[0], StringComparer.Ordinal))
            {
                if (group.Ids.Count >= 2) g.Unnamed.Add(group.Ids);
                else g.Alone.Add(group.Ids[0]);
            }
            return g;
        }

        private static float[] Normal(float[] v)
        {
            var length = Math.Sqrt(v.Sum(x => (double)x * x));
            return length == 0 ? v : v.Select(x => (float)(x / length)).ToArray();
        }
    }
}
