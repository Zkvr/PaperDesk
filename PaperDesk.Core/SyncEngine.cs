namespace PaperDesk.Core;

public sealed class CloudWriteRejectedException(Exception cause)
    : Exception(L.T("Сервер отклонил изменение заметки"), cause);

public sealed class SyncEngine(INoteStore store, ICloudNotes cloud)
{
    private readonly SemaphoreSlim gate = new(1);
    public DateTimeOffset? LastSuccess { get; private set; }
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var operation in store.Pending())
            {
                ct.ThrowIfCancellationRequested();
                PushResult result;
                try { result = await cloud.PushAsync(operation, ct); }
                catch (CloudWriteRejectedException) { store.MarkRejected(operation); continue; }
                if (result.Note == null) throw new InvalidDataException(L.T("Сервер вернул пустую заметку"));
                if (result.Status == "ok") store.Acknowledge(operation, result.Note);
                else if (result.Status == "conflict") store.SetConflict(operation, result.Note);
                else throw new InvalidDataException(L.T("Неизвестный ответ синхронизации"));
            }
            foreach (var note in await cloud.FetchAsync(ct)) store.ApplyRemote(note);
            var rejected = store.RejectedNotes().Count;
            if (rejected == 0) LastSuccess = DateTimeOffset.UtcNow;
            return rejected;
        }
        finally { gate.Release(); }
    }
}
