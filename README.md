# CSharpProlog [![NuGet Package](https://img.shields.io/nuget/v/CSProlog.svg)](https://www.nuget.org/packages/CSProlog/) [![Build status](https://ci.appveyor.com/api/projects/status/prufu2gwyb63l3ua?svg=true)](https://ci.appveyor.com/project/jsakamoto/csharpprolog)
A C# implementation of Prolog

```csharp
// PM> Install-Package CSProlog -pre
using System;
using Prolog;

class Program
{
    static void Main(string[] args)
    {
        var prolog = new PrologEngine(persistentCommandHistory: false);

        // 'socrates' is human.
        prolog.ConsultFromString("human(socrates).");
        // human is bound to die.
        prolog.ConsultFromString("mortal(X) :- human(X).");

        // Question: Shall 'socrates' die?
        var solution = prolog.GetFirstSolution(query: "mortal(socrates).");
        Console.WriteLine(solution.Solved); // = "True" (Yes!)
    }
}
```
## Installation

Run the following command from the Visual Studio Package Manager Console to install the latest version:

`Install-Package CSProlog`

The NuGet page can be found here:\
<https://www.nuget.org/packages/CSProlog/#>

## Solution Layout
### CSProlog
Prolog Engine

### CSProlog.Core.Test
Unit Tests

### PL.NETCore
Dotnet Core Console Interactive Interpreter (tested in linux and windows)

### PLd
DOS Console Interactive Interpreter

### PLw
Windows Forms Example

### PLx
An example of how to use the engine within another Program


## For more documents

Earlier release documents can be found in [README (2007-2014).pdf](README%20(2007-2014).pdf).

## Release Notes

### v.6.0.0

- BREAKING CHANGE: Remove "SAMPLES, TESTING & EXPERIMENTAL" predefined predicates. (including CHAT-80 support)
- Fix: "help" predefined predicate dose not work.
- Enhance: GetAllSolutions can work with null file name.

### v.5.0.0.1

- Support: .NET Standard 1.4 (.NET Core) and UWP

### v.5.0.0

- BREAKING CHANGE: Remove dependency of "System.Windows.Forms".
- NuGet package release

### Older versions

Earlier release notes can be found in [README (2007-2014).pdf](README%20(2007-2014).pdf).

## License

[GNU LGPL v.3](LICENSE)
