namespace GpuSuite.Authoring;

/// <summary>Small per-document snapshot history. Callers own one instance per active document; histories never cross drafts.</summary>
public sealed class DraftUndoHistory<T>
{
    private readonly int _capacity;
    private readonly List<T> _undo = [];
    private readonly List<T> _redo = [];
    private readonly Func<T, T> _clone;
    public DraftUndoHistory(Func<T, T> clone, int capacity = 40) { _clone = clone; _capacity = Math.Max(1, capacity); }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Capture(T value) { _undo.Add(_clone(value)); if (_undo.Count > _capacity) _undo.RemoveAt(0); _redo.Clear(); }
    public T Undo(T current) { if (!CanUndo) return current; _redo.Add(_clone(current)); var result=_undo[^1]; _undo.RemoveAt(_undo.Count-1); return _clone(result); }
    public T Redo(T current) { if (!CanRedo) return current; _undo.Add(_clone(current)); var result=_redo[^1]; _redo.RemoveAt(_redo.Count-1); return _clone(result); }
    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
