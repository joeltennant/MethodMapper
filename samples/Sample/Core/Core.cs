using Lib;
namespace Core;
public class Service
{
    private readonly IRepo<Customer> _repo;
    public Service(IRepo<Customer> repo) { _repo = repo; }
    public Customer Find(int id) => _repo.Get(id);
}
