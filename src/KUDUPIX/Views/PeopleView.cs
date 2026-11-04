using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kudupix.Controls;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>
    /// People: the people you've named, faces to ask about, and groups of faces not named yet ("Who is this?"). A group
    /// or a person opens on the same page.
    /// </summary>
    public sealed class PeopleView : Grid
    {
        private enum Page { Overview, Group, Person }

        private readonly MainWindow _main;
        private readonly StackPanel _content = new StackPanel { Margin = new Thickness(24, 14, 24, 24) };
        private Page _page = Page.Overview;
        private List<string> _group;
        private Person _person;
        private bool _showHidden;
        private readonly HashSet<string> _leftOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public PeopleView(MainWindow main)
        {
            _main = main;
            Children.Add(new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        }

        private PeopleWork Work => _main.People;
        private PeopleData Data => _main.Catalog.People;

        /// <summary>What's shown (for the self-test).</summary>
        public string PageName => _page.ToString();

        public void ShowOverview()
        {
            _page = Page.Overview;
            Fill();
        }

        public void ShowGroup(List<string> faces)
        {
            _page = Page.Group;
            _group = faces;
            _leftOut.Clear();
            Fill();
        }

        public void ShowPerson(Person person)
        {
            _page = Page.Person;
            _person = person;
            Fill();
        }

        /// <summary>Draws the page again (the groups changed).</summary>
        public void Fill()
        {
            _content.Children.Clear();
            if (_page == Page.Group && (_group == null || !Work.Groups.Unnamed.Any(g => g.Contains(_group[0])))) _page = Page.Overview;
            if (_page == Page.Person && (_person == null || Data.Find(_person.Id) == null)) _page = Page.Overview;
            if (_page == Page.Group) FillGroup();
            else if (_page == Page.Person) FillPerson();
            else FillOverview();
        }

        // ------------------------------------------------------------------ the overview

        private void FillOverview()
        {
            _content.Children.Add(Ui.Text("People", 18, bold: true));
            var status = Work.Problem ?? (Work.Busy ? $"Looking for people in your photos… {Work.Done:N0} of {Work.Total:N0}. You can name people already." :
                "Faces are looked for on this PC only; nothing is sent anywhere. Only faces big enough to tell who it is are grouped.");
            _content.Children.Add(Ui.Subtle(status, 12.5).Margin(0, 2, 0, 12));
            if (Work.Problem != null) return;

            var people = Data.People().Where(p => _showHidden || !p.Hidden).ToList();
            var g = Work.Groups;
            var asks = g.Ask.Where(x => !x.Key.Hidden).ToList();
            if (asks.Count > 0)
            {
                var links = new WrapPanel();
                foreach (var a in asks.OrderByDescending(a => a.Value.Count))
                {
                    var person = a.Key;
                    links.Children.Add(Ui.Link($"Is this {person.Name}? ({a.Value.Count:N0})", (s, e) => ShowPerson(person)).Margin(0, 0, 16, 0));
                }
                _content.Children.Add(Ui.Banner("IconUser", Ui.Column(Ui.Text("Some faces might be people you've named. Have a look:", 12.5), links)).Margin(0, 0, 0, 14));
            }

            if (people.Count > 0)
            {
                _content.Children.Add(Ui.Text("People you've named", 13, "SubtleText", bold: true).Margin(0, 4, 0, 8));
                var wrap = new WrapPanel();
                foreach (var p in people)
                {
                    var person = p;
                    var faces = g.FacesOf(p);
                    var tile = Tile(faces.FirstOrDefault(id => p.Faces.Contains(id)) ?? faces.FirstOrDefault(), p.Name + (p.Hidden ? " (hidden)" : string.Empty), Format.Count(Work.PhotoCount(p), "photo"),
                        () => _main.Go(ViewKind.Person, person.Id));
                    tile.ContextMenu = PersonMenu(person);
                    wrap.Children.Add(tile);
                }
                _content.Children.Add(wrap);
            }

            _content.Children.Add(Ui.Text("Who is this?", 13, "SubtleText", bold: true).Margin(0, 14, 0, 2));
            if (g.Unnamed.Count == 0)
                _content.Children.Add(Ui.Subtle(Work.Busy ? "Groups of faces appear here as photos are looked at." : "No more groups of faces to name.", 12.5).Margin(0, 4, 0, 0));
            else
            {
                _content.Children.Add(Ui.Subtle("The same face seen in more than one photo. Click a group to name it.", 12.5).Margin(0, 0, 0, 8));
                var wrap = new WrapPanel();
                foreach (var group in g.Unnamed.Take(120))
                {
                    var faces = group;
                    var photos = faces.Select(f => FaceStore.Split(f).Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    wrap.Children.Add(Tile(faces[0], "Who is this?", Format.Count(photos, "photo"), () => ShowGroup(faces)));
                }
                _content.Children.Add(wrap);
            }

            var hidden = Data.People().Count(p => p.Hidden);
            if (hidden > 0)
            {
                var toggle = Ui.Link(_showHidden ? "Don't show hidden people" : $"Show hidden people ({hidden})", (s, e) =>
                {
                    _showHidden = !_showHidden;
                    Fill();
                });
                toggle.HorizontalAlignment = HorizontalAlignment.Left;
                _content.Children.Add(toggle.Margin(0, 16, 0, 0));
            }
        }

        // ------------------------------------------------------------------ a group not named yet

        private void FillGroup()
        {
            var back = Ui.Link("‹ People", (s, e) => ShowOverview());
            back.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Children.Add(back);
            _content.Children.Add(Ui.Text("Who is this?", 18, bold: true).Margin(0, 6, 0, 2));
            _content.Children.Add(Ui.Subtle("Type their name. If one of these isn't the same person, click it to leave it out.", 12.5).Margin(0, 0, 0, 12));

            var row = Ui.Columns(Ui.Px(280), Ui.Auto, Ui.Star());
            var name = NameBox();
            row.Children.Add(name.At(0, 0));
            var ok = Ui.Button("Name them", "IconCheck", "PrimaryButton", (s, e) => NameGroup(name.Text));
            row.Children.Add(ok.Margin(8, 0, 0, 0).At(0, 1));
            _content.Children.Add(row.Margin(0, 0, 0, 14));
            name.KeyDown += (s, e) => { if (e.Key == Key.Enter) NameGroup(name.Text); };

            var wrap = new WrapPanel();
            foreach (var f in _group)
            {
                var face = f;
                var tile = Tile(face, null, _leftOut.Contains(face) ? "Left out" : null, () =>
                {
                    if (!_leftOut.Remove(face)) _leftOut.Add(face);
                    Fill();
                });
                if (_leftOut.Contains(face)) tile.Opacity = 0.4;
                tile.ContextMenu = FaceMenu(face, null);
                wrap.Children.Add(tile);
            }
            _content.Children.Add(wrap);
        }

        /// <summary>Names the group (the faces not left out). Returns the person.</summary>
        public Person NameGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var faces = _group.Where(f => !_leftOut.Contains(f)).ToList();
            if (faces.Count == 0) return null;
            var person = Data.Name(faces, name);
            _main.SetStatus($"Named {Format.Count(faces.Count, "face")} “{person.Name}”");
            ShowOverview();
            return person;
        }

        private ComboBox NameBox()
        {
            var box = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, MinHeight = 36 };
            foreach (var p in Data.People()) box.Items.Add(p.Name);
            System.Windows.Automation.AutomationProperties.SetName(box, "Name");
            box.Loaded += (s, e) => box.Focus();
            return box;
        }

        // ------------------------------------------------------------------ a named person

        private void FillPerson()
        {
            var p = _person;
            var back = Ui.Link("‹ People", (s, e) => ShowOverview());
            back.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Children.Add(back);
            var faces = Work.Groups.FacesOf(p);
            var head = Ui.Columns(Ui.Auto, Ui.Star());
            head.Children.Add(Round(faces.FirstOrDefault(id => p.Faces.Contains(id)) ?? faces.FirstOrDefault(), 64).At(0, 0));
            head.Children.Add(Ui.Column(Ui.Text(p.Name, 18, bold: true), Ui.Subtle(Format.Count(Work.PhotoCount(p), "photo") + (p.Hidden ? " · hidden" : string.Empty), 12.5)).Margin(14, 0, 0, 0).At(0, 1));
            ((FrameworkElement)head.Children[1]).VerticalAlignment = VerticalAlignment.Center;
            _content.Children.Add(head.Margin(0, 8, 0, 10));
            var buttons = new WrapPanel();
            buttons.Children.Add(Ui.Small(Ui.Button("See their photos", "IconPicture", "PrimaryButton", (s, e) => _main.Go(ViewKind.Person, p.Id))).Margin(0, 0, 8, 6));
            buttons.Children.Add(Ui.Small(Ui.Button("Rename…", "IconEdit", "SecondaryButton", (s, e) => Rename(p))).Margin(0, 0, 8, 6));
            buttons.Children.Add(Ui.Small(Ui.Button(p.Hidden ? "Show in People" : "Hide", null, "SecondaryButton", (s, e) => { Data.Hide(p, !p.Hidden); Fill(); })).Margin(0, 0, 8, 6));
            Button merge = null;
            merge = Ui.Small(Ui.Button("The same person as…", null, "SecondaryButton", (s, e) => MergeMenu(p, merge)));
            buttons.Children.Add(merge.Margin(0, 0, 8, 6));
            buttons.Children.Add(Ui.Small(Ui.Button("Forget this person", "IconTrash", "GhostButton", (s, e) => Forget(p))).Margin(0, 0, 8, 6));
            _content.Children.Add(buttons);

            if (Work.Groups.Ask.TryGetValue(p, out var asks) && asks.Count > 0)
            {
                _content.Children.Add(Ui.Text($"Is this {p.Name}?", 14, bold: true).Margin(0, 16, 0, 2));
                _content.Children.Add(Ui.Subtle("These faces look a little like them. Say yes or no; it helps with the rest.", 12.5).Margin(0, 0, 0, 8));
                var wrap = new WrapPanel();
                foreach (var (id, _) in asks.Take(60))
                {
                    var face = id;
                    var yes = Ui.Small(Ui.Button("Yes", null, "PrimaryButton", (s, e) => Data.Confirm(p, new[] { face })));
                    var no = Ui.Small(Ui.Button("No", null, "SecondaryButton", (s, e) => Data.Reject(p, face)));
                    var tile = Tile(face, null, null, () => OpenPhotoOf(face));
                    ((StackPanel)tile.Child).Children.Add(Ui.Row(yes, no.Margin(6, 0, 0, 0)).Margin(0, 6, 0, 0));
                    wrap.Children.Add(tile);
                }
                _content.Children.Add(wrap);
            }

            _content.Children.Add(Ui.Text($"Faces of {p.Name}", 14, bold: true).Margin(0, 16, 0, 2));
            _content.Children.Add(Ui.Subtle("Right-click a face that isn't them.", 12.5).Margin(0, 0, 0, 8));
            var all = new WrapPanel();
            foreach (var id in faces.Take(300))
            {
                var face = id;
                var tile = Tile(face, null, p.Faces.Contains(face) ? null : "Found by itself", () => OpenPhotoOf(face));
                tile.ContextMenu = FaceMenu(face, p);
                all.Children.Add(tile);
            }
            _content.Children.Add(all);
        }

        private void OpenPhotoOf(string faceId)
        {
            var item = _main.Catalog.Library.Find(FaceStore.Split(faceId).Path);
            if (item != null) _main.OpenAlone(item);
        }

        private void Rename(Person p)
        {
            var name = Dialogs.Ask(Window.GetWindow(this), "Rename", "A new name. If someone already has it, the two become one person.", p.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            Data.Rename(p, name);
            Fill();
        }

        private void MergeMenu(Person p, FrameworkElement at)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            foreach (var other in Data.People().Where(o => o != p))
            {
                var into = other;
                var mi = new MenuItem { Header = other.Name };
                mi.Click += (s, e) =>
                {
                    if (!Dialogs.Confirm(Window.GetWindow(this), $"{p.Name} and {into.Name} are the same person? Their faces are put together under “{into.Name}”.")) return;
                    Data.Merge(p, into);
                    ShowPerson(into);
                };
                menu.Items.Add(mi);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No one else is named yet", IsEnabled = false });
            menu.IsOpen = true;
        }

        private void Forget(Person p)
        {
            if (!Dialogs.Confirm(Window.GetWindow(this), $"Forget “{p.Name}”? Their photos stay; their faces go back to “Who is this?”.")) return;
            Data.Remove(p);
            ShowOverview();
        }

        private ContextMenu PersonMenu(Person p)
        {
            var menu = new ContextMenu();
            void Add(string text, Action run)
            {
                var mi = new MenuItem { Header = text };
                mi.Click += (s, e) => run();
                menu.Items.Add(mi);
            }
            Add("Faces and questions", () => ShowPerson(p));
            Add("Rename…", () => Rename(p));
            Add(p.Hidden ? "Show in People" : "Hide", () => { Data.Hide(p, !p.Hidden); Fill(); });
            Add("Forget this person", () => Forget(p));
            return menu;
        }

        /// <summary>The menu for one face: who it is, not them, not a face.</summary>
        public ContextMenu FaceMenu(string faceId, Person current)
        {
            var menu = new ContextMenu();
            foreach (var p in Data.People().Where(x => x != current).Take(20))
            {
                var person = p;
                var mi = new MenuItem { Header = "This is " + p.Name };
                mi.Click += (s, e) => Data.Confirm(person, new[] { faceId });
                menu.Items.Add(mi);
            }
            var someone = new MenuItem { Header = "This is someone else…" };
            someone.Click += (s, e) =>
            {
                var name = Dialogs.Ask(Window.GetWindow(this), "Who is this?", "Their name.", string.Empty);
                if (!string.IsNullOrWhiteSpace(name)) Data.Name(new[] { faceId }, name);
            };
            menu.Items.Add(someone);
            menu.Items.Add(new Separator());
            if (current != null)
            {
                var not = new MenuItem { Header = "Not " + current.Name };
                not.Click += (s, e) => Data.Reject(current, faceId);
                menu.Items.Add(not);
            }
            var notFace = new MenuItem { Header = "Not a face" };
            notFace.Click += (s, e) => Data.NotAFace(faceId);
            menu.Items.Add(notFace);
            return menu;
        }

        // ------------------------------------------------------------------ face tiles

        private Border Tile(string faceId, string title, string note, Action click)
        {
            var panel = new StackPanel { Width = 112 };
            var round = Round(faceId, 96);
            round.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(round);
            if (!string.IsNullOrEmpty(title))
            {
                var t = Ui.Text(title, 12.5, bold: true);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.HorizontalAlignment = HorizontalAlignment.Center;
                panel.Children.Add(t.Margin(0, 6, 0, 0));
            }
            if (!string.IsNullOrEmpty(note))
            {
                var n = Ui.Subtle(note, 11.5, false);
                n.HorizontalAlignment = HorizontalAlignment.Center;
                panel.Children.Add(n);
            }
            var b = new Border { Child = panel, Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 12), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            b.MouseEnter += (s, e) => b.Res(Border.BackgroundProperty, "HoverBg");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => click?.Invoke();
            if (title != null) System.Windows.Automation.AutomationProperties.SetName(b, title);
            return b;
        }

        private FrameworkElement Round(string faceId, double size)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), ClipToBounds = true }.Res(Border.BackgroundProperty, "SurfaceAltBg");
            var holder = new Grid { Width = size, Height = size };
            holder.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
            holder.Children.Add(circle);
            holder.Children.Add(image);
            if (faceId != null)
            {
                var ready = Work.Crop(faceId, b => image.Source = b);
                if (ready != null) image.Source = ready;
            }
            return holder;
        }
    }
}
