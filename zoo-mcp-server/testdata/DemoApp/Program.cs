namespace DemoApp;

public interface IGreeter
{
    string Greet(string name);
}

public class Greeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}!";
}

public class FancierGreeter : IGreeter
{
    private readonly Greeter _inner = new Greeter();
    public string Greet(string name) => _inner.Greet(name).ToUpperInvariant() + " ✨";
}

public static class Program
{
    public static void Main()
    {
        IGreeter g = new Greeter();
        var msg = g.Greet("world");
        var f = new FancierGreeter();
        System.Console.WriteLine(msg + f.Greet("zoo"));
    }
}
