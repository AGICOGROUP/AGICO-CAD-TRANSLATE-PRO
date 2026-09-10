using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CadTranslation.AutoCAD2025;

/// <summary>Read-only compatibility boundary; required text never silently disappears.</summary>
internal sealed class CadObjectAccess(Database database, Transaction transaction, List<CadObjectAccess.Issue>? issues = null)
{
    internal sealed record Issue(string Stage, string? Block, string? Handle, string? ParentHandle,
        string ObjectType, string Error, string? RecordId, string? Detail);

    internal T? Read<T>(ObjectId id, string stage, string? block = null, string? parentHandle = null,
        string? recordId = null, bool required = false) where T : DBObject
    {
        string? handle = null;
        string objectType = typeof(T).Name;
        T? Problem(string error, System.Exception? exception = null, bool fatal = false)
        {
            var issue = new Issue(stage, block, handle, parentHandle, objectType, error, recordId, exception?.ToString());
            issues?.Add(issue);
            if (!required && !fatal) return null;
            var failure = new CommandProtocolException("cad_object_access_failed",
                $"{stage}: {error}; block={block}; parent={parentHandle}; handle={handle ?? "<null>"}; type={objectType}; record={recordId}", exception);
            failure.Data["stage"] = stage;
            failure.Data["block"] = block;
            failure.Data["handle"] = handle;
            failure.Data["parentHandle"] = parentHandle;
            failure.Data["objectType"] = objectType;
            failure.Data["recordId"] = recordId;
            throw failure;
        }
        try
        {
            if (id.IsNull) return Problem("NullObjectId");
            if (!id.IsValid) return Problem("InvalidObjectId");
            if (id.Database != database) return Problem("WrongDatabase");
            handle = id.Handle.ToString();
            if (id.IsErased || id.IsEffectivelyErased) return Problem("ErasedObject");
            objectType = id.ObjectClass?.Name ?? objectType;
            return transaction.GetObject(id, OpenMode.ForRead, false) is T value ? value : Problem("WrongObjectType");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception exception)
        {
            string error = exception.ErrorStatus.ToString();
            return Problem(error, exception, fatal: error is not
                ("NullObjectId" or "InvalidObjectId" or "WasErased" or "PermanentlyErased" or "WrongDatabase"));
        }
    }

    internal bool TryVertices(IEnumerable<ObjectId> ids, string stage, string parentHandle, string? block,
        out Point3d[] vertices)
    {
        var points = new List<Point3d>();
        foreach (ObjectId id in ids)
        {
            var vertex = Read<Vertex2d>(id, stage, block, parentHandle);
            if (vertex is null) { vertices = []; return false; }
            points.Add(vertex.Position);
        }
        vertices = points.ToArray();
        return true;
    }
}
