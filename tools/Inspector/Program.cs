using System;
using System.Linq;
using System.Reflection;
using SomeECS.Core;
using SomeECS.Core.Components;

struct TestComp : IComponent { }

class Program
{
    static void Main()
    {
        Console.WriteLine("\nInspecting SomeECS World component API:");
        foreach (var method in typeof(World)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name is "Add" or "Set" or "Read" or "ReadWrite" or "Has"))
        {
            Console.WriteLine("Method: " + method.Name);
        }
    }
}
