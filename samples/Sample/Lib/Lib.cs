namespace Lib;
public interface IRepo<T> { T Get(int id); }
public class Customer { }
public class CustomerRepo : IRepo<Customer>
{
    public Customer Get(int id) => Load(id);
    private Customer Load(int id) => new Customer();
}
public static class Helpers
{
    public static int Twice(int x) => Inner(x) * 2;
    private static int Inner(int x) => x;
    public static string Shout(this string s) => Decorate(s.ToUpper());
    private static string Decorate(string s) => s + "!";
}
