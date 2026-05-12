# GetSet

A Roslyn source generator that lets you write **inline field + property shorthand** in C#.

Instead of the usual boilerplate:

```csharp
private int _num = 5;

public int num
{
    get { return _num; }
    set { if (value >= 0) _num = value; }
}
```

You write:

```csharp
public int num = 5
{
    get { return num; }
    set { if (value >= 0) num = value; }
}
```

The generator runs inside the Roslyn compiler pipeline and automatically emits the backing field and full property — with **full IDE support, IntelliSense, and correct debug line numbers**.

---

## Installation

```
dotnet add package GetSet
```

Or via NuGet Package Manager:

```
Install-Package GetSet
```

---

## Quick Start

1. Add the package to your project.
2. Make your class `partial`.
3. Write the shorthand syntax.

```csharp
using GetSet; // optional — gives you the [UseGetSet] attribute

public partial class Player
{
    // Inline field + property with custom validation
    public int health = 100
    {
        get { return health; }
        set { health = Math.Clamp(value, 0, 100); }
    }

    // Getter-only (read-only computed-style property)
    public string displayName = "Hero"
    {
        get { return displayName.ToUpper(); }
    }

    // Static property
    public static int instanceCount = 0
    {
        get { return instanceCount; }
        set { if (value >= 0) instanceCount = value; }
    }
}
```

The generator emits (in a `*.GetSet.g.cs` file that you never touch):

```csharp
public partial class Player
{
    private int _health = 100;
    public int health
    {
        get { return Math.Clamp(_health, 0, 100); }
        set { _health = Math.Clamp(value, 0, 100); }
    }

    private string _displayName = "Hero";
    public string displayName
    {
        get { return _displayName.ToUpper(); }
    }

    private static int _instanceCount = 0;
    public static int instanceCount
    {
        get { return _instanceCount; }
        set { if (value >= 0) _instanceCount = value; }
    }
}
```

---

## Rules & Requirements

| Rule | Detail |
|------|--------|
| Class must be `partial` | The generator adds a second partial definition |
| One variable per declaration | `public int x = 1, y = 2 { … }` is **not** supported |
| Must have a `get` accessor | A `set`-only block is not valid |
| Field references auto-rewritten | Inside accessor bodies, `num` → `_num` automatically |
| Works with structs too | `partial struct` is fully supported |
| Generics supported | `partial class Box<T>` works fine |

---

## How It Works

Roslyn parses the shorthand as a **field with trailing garbage** (the `{ get … set … }` block becomes skipped-token trivia). The generator:

1. Detects skipped-token trivia after a field declaration.
2. Re-parses just the skipped fragment as a C# block.
3. Extracts the getter and setter bodies.
4. Emits a `partial class` file with the backing field and property.

Because this runs as a **Roslyn Incremental Source Generator**, re-generation only happens when the affected file changes — keeping build times fast.

---

## Optional Attribute

You can mark a class with `[UseGetSet]` to make the intent explicit:

```csharp
using GetSet;

[UseGetSet]
public partial class MyClass
{
    // …
}
```

This is purely documentary — the generator activates with or without it.

---

## Suppressing Warnings

Because the shorthand isn't valid plain C#, Roslyn will show `CS1001` / `CS1002` parse errors in the raw field line. Add this to your `.csproj` to silence them:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);CS1001;CS1002;CS1513</NoWarn>
</PropertyGroup>
```

---

## Building From Source

```bash
git clone https://github.com/YOUR_USERNAME/GetSet
cd GetSet
dotnet build
dotnet test
```

Pack the NuGet:

```bash
dotnet pack src/GetSet/GetSet.csproj -c Release -o ./nupkg
```

---

## License

MIT
