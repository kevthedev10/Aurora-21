using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aurora21.UltimateTeam.Core.Helpers;

/// <summary>
/// Random choices for packs, player picks, Draft and Squad Battles, on top of <see cref="Random"/>.
/// <see cref="Shared"/> is unpredictable and thread-safe. A <see cref="Seeded"/> generator is
/// deterministic, so a Draft run regenerates the same choices from its stored seed and the
/// day's Squad Battles opponents stay stable; seeded instances are not thread-safe.
/// </summary>
public sealed class GameRandom
{
    private readonly Random _random;

    private GameRandom(Random random) => _random = random;

    public static GameRandom Shared { get; } = new(Random.Shared);

    /// <summary>
    /// A deterministic generator for one named stream, e.g. <c>Seeded("draft-formations", runSeed)</c>.
    /// The parts are hashed, so different streams from the same run seed are independent.
    /// </summary>
    public static GameRandom Seeded(string stream, params object[] parts)
    {
        var key = string.Join(':', parts.Select(part => Convert.ToString(part, CultureInfo.InvariantCulture)).Prepend(stream));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        // Random(int) keeps its seeded sequence stable across .NET versions for compatibility.
        return new GameRandom(new Random(BitConverter.ToInt32(hash, 0)));
    }

    /// <summary>A uniform value in [0, 1).</summary>
    public double NextDouble() => _random.NextDouble();

    public long NextInt64(long minInclusive, long maxExclusive) => _random.NextInt64(minInclusive, maxExclusive);

    public T Choice<T>(IReadOnlyList<T> items) =>
        items.Count > 0 ? items[_random.Next(items.Count)] : throw new InvalidOperationException("Cannot choose from an empty list");

    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary><paramref name="count"/> distinct elements in random order.</summary>
    public List<T> Sample<T>(IReadOnlyList<T> population, int count)
    {
        if (count < 0 || count > population.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Sample larger than population or is negative");
        }
        var pool = population.ToArray();
        for (var i = 0; i < count; i++)
        {
            var j = _random.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool.Take(count).ToList();
    }

    /// <summary>One element, chosen with probability proportional to its weight.</summary>
    public T WeightedChoice<T>(IReadOnlyList<T> items, IReadOnlyList<double> weights)
    {
        if (items.Count != weights.Count)
        {
            throw new ArgumentException("The number of weights does not match the items");
        }
        var total = weights.Sum();
        if (!(total > 0) || !double.IsFinite(total))
        {
            throw new ArgumentException("Total of weights must be positive and finite");
        }
        var target = _random.NextDouble() * total;
        for (var i = 0; i < items.Count; i++)
        {
            target -= weights[i];
            if (target < 0)
            {
                return items[i];
            }
        }
        // Rounding can leave a sliver past the last weight; it belongs to the last positive weight.
        for (var i = items.Count - 1; ; i--)
        {
            if (weights[i] > 0)
            {
                return items[i];
            }
        }
    }
}
