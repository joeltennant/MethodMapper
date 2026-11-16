using System.Text.RegularExpressions;
using Lib;
using Core;
namespace App;

public class Root
{
    public void Run()
    {
        CrossProject(); Extension(); LocalFn(); Foreach(); Using(); PrimaryCtor(); ImplicitCtor();
        Recursion(2); Virtual(); Lambda(); Generated(); MultiTarget(); Props();
        var t = AsyncCall();
    }
    void CrossProject() { var svc = new Service(new CustomerRepo()); svc.Find(1); Helpers.Twice(2); }
    void Extension() { "x".Shout(); }
    void LocalFn() { int Local(int x) => Leaf1(x); Local(1); }
    int Leaf1(int x) => x;
    void Foreach() { foreach (var i in new Numbers()) { } }
    void Using() { using (new Res()) { } }
    void PrimaryCtor() { var p = new WithPrimary(3); }
    void ImplicitCtor() { var e = new NoCtor(); }
    void Recursion(int n) { if (n > 0) Ping(n); }
    void Ping(int n) { Pong(n - 1); }
    void Pong(int n) { Recursion(n); }
    void Virtual() { Shape s = new Circle(); s.Area(); }
    void Lambda() { Action a = () => LambdaTarget(); a(); }
    void LambdaTarget() { }
    void Generated() { Rx.A().IsMatch("aa"); }
    void MultiTarget() { new Multi.M().Go(); }
    void Props() { var b = new Bag(); b.Count = 3; var c = b.Count; }
    async Task AsyncCall() { await Task.Delay(1); await Mine(); }
    Task Mine() => Task.CompletedTask;
}
public class Numbers : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() { Produce(); yield return 1; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    void Produce() { }
}
public class Res : IDisposable { public void Dispose() { Cleanup(); } void Cleanup() { } }
public class WithPrimary(int x) { readonly int y = InitPrimary(x); static int InitPrimary(int v) => v; }
public class NoCtor { readonly int z = ComputeField(); static int ComputeField() => 1; }
public abstract class Shape { public abstract double Area(); }
public class Circle : Shape { public override double Area() => CircleMath(); double CircleMath() => 3.14; }
public class Square : Shape { public override double Area() => SquareMath(); double SquareMath() => 1; }
public class Bag { public int Count { get => Calc(); set { Store(value); } } int Calc() => 0; void Store(int v) { } }
public partial class Rx { [GeneratedRegex("a+")] public static partial Regex A(); }
