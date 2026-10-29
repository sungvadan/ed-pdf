using System;
using System.Collections.Generic;

namespace PdfSvd;

/// Undo and redo of document changes. Each entry knows how to undo and redo itself.
sealed class UndoStack
{
    sealed record Entry(Action Undo, Action Redo);

    readonly Stack<Entry> undo = new(), redo = new();

    public event Action? Changed;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// Records a change that has just been made.
    public void Push(Action undoAction, Action redoAction)
    {
        undo.Push(new Entry(undoAction, redoAction));
        redo.Clear();
        Changed?.Invoke();
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
