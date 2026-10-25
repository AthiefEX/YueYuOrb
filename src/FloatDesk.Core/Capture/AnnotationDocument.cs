using FloatDesk.Core.Models;

namespace FloatDesk.Core.Capture;

public sealed class AnnotationDocument
{
    private readonly List<Annotation> _items = [];
    private readonly Stack<IReadOnlyList<Annotation>> _undo = [];
    private readonly Stack<IReadOnlyList<Annotation>> _redo = [];

    public IReadOnlyList<Annotation> Items => _items.ToArray();

    public void Add(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        _undo.Push(_items.ToArray());
        _items.Add(annotation);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_items.ToArray());
        Restore(_undo.Pop());
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(_items.ToArray());
        Restore(_redo.Pop());
        return true;
    }

    private void Restore(IReadOnlyList<Annotation> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }
}
