using System.Reflection;
using System.Text;

namespace SimdImaging.Tests;

/// <summary>
/// Locks the shipped public surface. A change to it must show up as a diff in <c>docs/public-api.txt</c>, which
/// is the review gate the repository chose over a build-time analyzer: no package, no rule noise, and the diff
/// is readable.
/// <para>
/// Regenerate with <c>SIMDIMAGING_UPDATE_PUBLIC_API=1 dotnet test --filter PublicApi</c>, then read the diff
/// before committing it.
/// </para>
/// </summary>
public class PublicApiTests
{
    private static readonly NullabilityInfoContext Nullability = new();

    private static readonly Assembly[] Assemblies =
    [
        typeof(SimdImaging.Abstractions.PixelView).Assembly,
        typeof(SimdImaging.Processing.PointOps).Assembly,
        typeof(Image).Assembly,
    ];

    [Fact]
    public void PublicApi_MatchesTheCheckedInSurface()
    {
        var actual = Describe();
        var path = Path.Combine(RepositoryRoot(), "docs", "public-api.txt");

        if (Environment.GetEnvironmentVariable("SIMDIMAGING_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllLines(path, actual);
            return;
        }

        File.Exists(path).ShouldBeTrue($"{path} is missing; regenerate it with SIMDIMAGING_UPDATE_PUBLIC_API=1.");

        var expected = File.ReadAllLines(path);
        var difference = DescribeDifference(expected, actual);
        difference.ShouldBeEmpty(difference);
    }

    private static string DescribeDifference(string[] expected, string[] actual)
    {
        var missing = expected.Except(actual).Take(20).ToList();
        var added = actual.Except(expected).Take(20).ToList();
        if (missing.Count == 0 && added.Count == 0)
            return string.Empty;

        var report = new StringBuilder("The public API changed.\n");
        foreach (var line in missing)
            report.Append("  - ").Append(line).Append('\n');
        foreach (var line in added)
            report.Append("  + ").Append(line).Append('\n');
        report.Append("Regenerate with SIMDIMAGING_UPDATE_PUBLIC_API=1 and review the diff.");
        return report.ToString();
    }

    private static string[] Describe()
    {
        var lines = new List<string>();

        foreach (var assembly in Assemblies)
        {
            foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsNested && !type.IsNestedPublic)
                    continue;

                lines.Add($"{Kind(type)} {Name(type)}");

                if (type.IsEnum)
                {
                    foreach (var value in Enum.GetValues(type))
                        lines.Add($"  {Name(type)}.{value} = {Convert.ToInt64(value)}");
                    continue;
                }

                const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                              BindingFlags.DeclaredOnly;

                foreach (var constructor in type.GetConstructors(Declared))
                    lines.Add($"  {Name(type)}.#ctor({Parameters(constructor)})");

                foreach (var property in type.GetProperties(Declared))
                {
                    if (property.Name.Contains('<'))
                        continue;

                    var accessors = (property.GetMethod is not null ? "get; " : string.Empty)
                                    + (property.SetMethod is not null ? "set; " : string.Empty);
                    lines.Add($"  {Name(type)}.{property.Name} : {Friendly(property.PropertyType)} {{ {accessors}}}");
                }

                foreach (var field in type.GetFields(Declared))
                {
                    if (field.Name.Contains('<'))
                        continue;

                    lines.Add($"  {Name(type)}.{field.Name} : {Friendly(field.FieldType)}");
                }

                foreach (var method in type.GetMethods(Declared))
                {
                    if (method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal))
                        continue;

                    // Compiler-synthesised names (<Clone>$) are not part of the contract and move between
                    // compiler versions.
                    if (method.Name.Contains('<'))
                        continue;

                    lines.Add($"  {Name(type)}.{method.Name}{Generic(method)}({Parameters(method)}) -> {Returns(method)}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines.ToArray();
    }

    private static string Kind(Type type) => type.IsEnum ? "enum"
        : type.IsValueType ? "struct"
        : type.IsInterface ? "interface"
        : typeof(Delegate).IsAssignableFrom(type) ? "delegate"
        : "class";

    private static string Name(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    private static string Generic(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? $"<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>"
            : string.Empty;

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(p => $"{Signature(p.ParameterType, () => Nullability.Create(p).ReadState)} {p.Name}"));

    private static string Returns(MethodInfo method) =>
        Signature(method.ReturnType, () => Nullability.Create(method.ReturnParameter).ReadState);

    /// <summary>Annotates a reference type with <c>?</c> when it is nullable; a nullable value type carries its
    /// own <c>?</c> through <see cref="Friendly"/>.</summary>
    private static string Signature(Type type, Func<NullabilityState> state)
    {
        var name = Friendly(type);

        // A nullable value type already reads as T?, and a by-ref has no nullability of its own.
        if (type.IsValueType || type.IsByRef)
            return name;

        try
        {
            return state() == NullabilityState.Nullable ? name + "?" : name;
        }
        catch (InvalidOperationException)
        {
            return name;
        }
    }

    private static string Friendly(Type type)
    {
        if (type.IsByRef)
            return Friendly(type.GetElementType()!) + "&";

        if (type.IsArray)
            return Friendly(type.GetElementType()!) + "[]";

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Nullable<>))
                return Friendly(type.GetGenericArguments()[0]) + "?";

            var name = (definition.FullName ?? definition.Name).Split('`')[0];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Friendly))}>";
        }

        return type.FullName ?? type.Name;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimdImaging.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("SimdImaging.slnx was not found above the test output.");
    }
}
