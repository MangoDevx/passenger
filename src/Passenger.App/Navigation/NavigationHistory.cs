using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A place the caret was: its file, its offset in the text and that offset's line.</summary>
public sealed record HistoryPlace(string Path, int Offset, int Line);

/// <summary>Back and Forward, Last and Next Edit Location, and the places Recent Locations lists. A navigation (go to
/// definition, a usage, a bookmark) puts the place it left behind and forgets what was ahead; Back puts the caret's
/// place ahead. The caret moved by a key or a click changes neither, so Forward still goes where Back came from. An
/// edit keeps its place among the places edited, which Last Edit Location steps back through and Next Edit Location
/// forward. Each list keeps at most <see cref="Limit"/> places, and the places in an open file move with its
/// text.</summary>
public sealed class NavigationHistory
{
    /// <summary>How many places each way.</summary>
    public const int Limit = 150;

    /// <summary>Two places in a file fewer lines apart than this are one.</summary>
    private const int NearLines = 4;

    private readonly List<HistoryPlace> behind = [];
    private readonly List<HistoryPlace> ahead = [];

    /// <summary>The places left, by a navigation or for another file, and those edited, the latest last: Recent Locations'.</summary>
    private readonly List<Kept> visited = [];
    private readonly List<Kept> edited = [];

    /// <summary>Where Last and Next Edit Location stand among the places edited: past the last until they are used, and
    /// put back there by the next edit.</summary>
    private int editStep;
    private long count;

    /// <summary>A navigation left a place: it goes behind, taking the place of the last there when near it.</summary>
    public void Left(HistoryPlace from)
    {
        PutBehind(from);
        ahead.Clear();
        Visited(from);
    }

    /// <summary>A place left otherwise (another file shown): Recent Locations lists it, Back does not go there.</summary>
    public void Visited(HistoryPlace place) => Merge(visited, place);

    /// <summary>A place edited: Last Edit Location goes back to it from anywhere else, starting again from the latest.</summary>
    public void Edited(HistoryPlace place)
    {
        Merge(edited, place);
        editStep = edited.Count;
    }

    /// <summary>Whether Last (a step of -1) or Next Edit Location (1) has a place edited to go to.</summary>
    public bool CanStepEdits(int step) => step < 0 ? editStep > 0 && edited.Count > 0 : editStep < edited.Count - 1;

    /// <summary>The place edited before (a step of -1) or after (1) the one Last Edit Location went to last, passing over
    /// the caret's place and those whose file is gone; null with none.</summary>
    public HistoryPlace? StepEdits(int step, HistoryPlace? here, Func<string, bool> exists)
    {
        for (int i = Math.Min(editStep, edited.Count) + step; i >= 0 && i < edited.Count; i += step)
        {
            HistoryPlace place = edited[i].Place;
            if ((here is null || !Near(here, place)) && exists(place.Path))
            {
                editStep = i;
                return place;
            }
        }
        return null;
    }

    /// <summary>Recent Locations' places, the latest first: those left and edited, or only those edited, one for each
    /// stretch of a file, none near the caret's place.</summary>
    public IReadOnlyList<HistoryPlace> Recent(bool editedOnly, HistoryPlace? here)
    {
        List<HistoryPlace> listed = [];
        foreach (Kept recent in (editedOnly ? edited : visited.Concat(edited)).OrderByDescending(p => p.When))
        {
            if ((here is null || !Near(here, recent.Place)) && listed.All(l => !Near(l, recent.Place)))
            {
                listed.Add(recent.Place);
            }
        }
        return listed;
    }

    /// <summary>The files edited, the latest first.</summary>
    public IReadOnlyList<string> EditedFiles => [.. edited.OrderByDescending(p => p.When).Select(p => p.Place.Path).Distinct()];

    /// <summary>Forgets a place Recent Locations listed (its Delete), with those near it.</summary>
    public void Forget(HistoryPlace place)
    {
        visited.RemoveAll(p => Near(p.Place, place));
        int before = edited.Count;
        edited.RemoveAll(p => Near(p.Place, place));
        if (edited.Count != before)
        {
            editStep = edited.Count;
        }
    }

    /// <summary>Where Back goes from the caret's place, which goes ahead; null with nothing behind. A place whose file
    /// is gone is passed over.</summary>
    public HistoryPlace? Back(HistoryPlace? here, Func<string, bool> exists)
    {
        if (Take(behind, exists) is not { } target)
        {
            return null;
        }
        if (here is not null)
        {
            Keep(ahead, here);
        }
        return target;
    }

    /// <summary>Where Forward goes from the caret's place, which goes behind; null with nothing ahead. A place near the
    /// caret's is passed over while there is another.</summary>
    public HistoryPlace? Forward(HistoryPlace? here, Func<string, bool> exists)
    {
        HistoryPlace? target = Take(ahead, exists);
        while (target is not null && here is not null && Near(here, target) && ahead.Count > 0)
        {
            target = Take(ahead, exists) ?? target;
        }
        if (target is null)
        {
            return null;
        }
        if (here is not null && (here.Path != target.Path || here.Line != target.Line))
        {
            PutBehind(here);
        }
        return target;
    }

    /// <summary>Carries a file's places through an edit to it. Text typed at a place goes before it, so a place stays
    /// on the text it was at. Only a place that moved is made anew: every key comes here, and most places are in other
    /// files or before the key.</summary>
    public void Follow(string path, DocumentChangedEventArgs change)
    {
        Carry(behind);
        Carry(ahead);
        CarryRecent(visited);
        CarryRecent(edited);

        void Carry(List<HistoryPlace> places)
        {
            for (int i = 0; i < places.Count; i++)
            {
                places[i] = Carried(places[i]);
            }
        }

        void CarryRecent(List<Kept> places)
        {
            for (int i = 0; i < places.Count; i++)
            {
                HistoryPlace place = places[i].Place;
                HistoryPlace moved = Carried(place);
                if (!ReferenceEquals(moved, place))
                {
                    places[i] = places[i] with { Place = moved };
                }
            }
        }

        HistoryPlace Carried(HistoryPlace place)
        {
            if (place.Path != path)
            {
                return place;
            }
            int mapped = TextEditing.MapOffset(Math.Min(place.Offset, change.Before.Length), change.Edits, afterInsertions: true);
            int offset = Math.Min(mapped, change.After.Length);
            int line = change.After.GetLineFromOffset(offset);
            return offset == place.Offset && line == place.Line ? place : place with { Offset = offset, Line = line };
        }
    }

    /// <summary>Carries the places in a file, or in every file of a folder, to where it moved.</summary>
    public void Move(string from, string to)
    {
        foreach (List<HistoryPlace> places in new[] { behind, ahead })
        {
            for (int i = 0; i < places.Count; i++)
            {
                places[i] = places[i] with { Path = PathNames.Moved(places[i].Path, from, to) };
            }
        }
        foreach (List<Kept> places in new[] { visited, edited })
        {
            for (int i = 0; i < places.Count; i++)
            {
                places[i] = places[i] with { Place = places[i].Place with { Path = PathNames.Moved(places[i].Place.Path, from, to) } };
            }
        }
    }

    /// <summary>Forgets every place, as another folder opens: each folder's are its own.</summary>
    public void Clear()
    {
        behind.Clear();
        ahead.Clear();
        visited.Clear();
        edited.Clear();
        editStep = 0;
    }

    /// <summary>Keeps a place as the latest of its list, in place of the last when near it.</summary>
    private void Merge(List<Kept> places, HistoryPlace place)
    {
        if (places.Count > 0 && Near(places[^1].Place, place))
        {
            places.RemoveAt(places.Count - 1);
        }
        places.Add(new Kept(place, ++count));
        if (places.Count > Limit)
        {
            places.RemoveAt(0);
        }
    }

    private void PutBehind(HistoryPlace place)
    {
        if (behind.Count > 0 && Near(behind[^1], place))
        {
            behind.RemoveAt(behind.Count - 1);
        }
        Keep(behind, place);
    }

    private static bool Near(HistoryPlace one, HistoryPlace other) => one.Path == other.Path && Math.Abs(one.Line - other.Line) < NearLines;

    private static void Keep(List<HistoryPlace> places, HistoryPlace place)
    {
        places.Add(place);
        if (places.Count > Limit)
        {
            places.RemoveAt(0);
        }
    }

    private static HistoryPlace? Take(List<HistoryPlace> places, Func<string, bool> exists)
    {
        while (places.Count > 0)
        {
            HistoryPlace last = places[^1];
            places.RemoveAt(places.Count - 1);
            if (exists(last.Path))
            {
                return last;
            }
        }
        return null;
    }

    /// <summary>A place Recent Locations may list, and how recently it was kept, counted over both of its lists.</summary>
    private sealed record Kept(HistoryPlace Place, long When);
}
