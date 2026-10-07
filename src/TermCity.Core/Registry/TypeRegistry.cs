using System.Collections;

namespace TermCity.Core.Registry;

/// <summary>Base class for anything that is registered by name and stored in the map as a compact byte id.</summary>
public abstract class RegisteredType
{
    public required string Name { get; init; }

    /// <summary>Assigned by the registry. Ids are an in-memory detail; saves store names.</summary>
    public byte Id { get; internal set; }

    public override string ToString() => Name;
}

/// <summary>Ordered collection of registered types. New terrains, features and buildings are added through these.</summary>
public class TypeRegistry<T> : IEnumerable<T> where T : RegisteredType
{
    private readonly List<T> _items = [];
    private readonly Dictionary<string, T> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _firstId;

    protected TypeRegistry(int firstId) => _firstId = firstId;

    public int Count => _items.Count;

    public T Register(T item)
    {
        if (_byName.ContainsKey(item.Name))
        {
            throw new InvalidOperationException($"'{item.Name}' is already registered.");
        }

        int id = _firstId + _items.Count;
        if (id > byte.MaxValue)
        {
            throw new InvalidOperationException("Too many registered types.");
        }

        item.Id = (byte)id;
        _items.Add(item);
        _byName[item.Name] = item;
        return item;
    }

    /// <summary>
    /// Lets an old name keep resolving to a renamed type, so saves and callers from before a rename still work.
    /// The alias is never written back out: saves always store the current name.
    /// </summary>
    public void Alias(string oldName, string currentName)
    {
        var target = Get(currentName);
        _byName[oldName] = target;
    }

    public T this[byte id]
    {
        get
        {
            int index = id - _firstId;
            if (index < 0 || index >= _items.Count)
            {
                throw new KeyNotFoundException($"No registered type with id {id}.");
            }

            return _items[index];
        }
    }

    public T? Find(string name) => _byName.GetValueOrDefault(name);

    public T Get(string name) => Find(name) ?? throw new KeyNotFoundException($"'{name}' is not registered.");

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
