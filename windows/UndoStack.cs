using System;
using System.Collections.Generic;

namespace EdPdf;

/// Undo and redo of document changes. Each entry knows how to undo and redo itself.
sealed class UndoStack
{
    sealed record Entry(Action Undo, Action Redo);

    readonly Stack<Entry> undo = new(), redo = new();
    /// Changes recorded inside Group, undone together.
    List<Entry>? grouped;

    public event Action? Changed;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// Records a change that has just been made.
    public void Push(Action undoAction, Action redoAction)
    {
        if (grouped != null)
        {
            grouped.Add(new Entry(undoAction, redoAction));
            return;
        }
        undo.Push(new Entry(undoAction, redoAction));
        redo.Clear();
        Changed?.Invoke();
    }

    /// Runs `changes` and records what they push as one undoable change.
    public void Group(Action changes)
    {
        if (grouped != null) { changes(); return; }
        var entries = grouped = [];
        try { changes(); }
        finally { grouped = null; }
        if (entries.Count == 0) return;
        Push(() => { for (var i = entries.Count - 1; i >= 0; i--) entries[i].Undo(); },
             () => { foreach (var entry in entries) entry.Redo(); });
    }

    public void Undo()
    {
        if (!undo.TryPop(out var entry)) return;
        entry.Undo();
        redo.Push(entry);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!redo.TryPop(out var entry)) return;
        entry.Redo();
        undo.Push(entry);
        Changed?.Invoke();
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        Changed?.Invoke();
    }
}
