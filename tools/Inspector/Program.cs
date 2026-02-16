using System;
using System.Reflection;
using Friflo.Engine.ECS;

struct TestComp : IComponent { }

class Program
{
    static void Main()
    {
        Console.WriteLine("\nInspecting ComponentTypes:");
        foreach (var method in typeof(ComponentTypes).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
             Console.WriteLine("Method: " + method.Name);
        }
    }
}
