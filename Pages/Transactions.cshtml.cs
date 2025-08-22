using Microsoft.AspNetCore.Mvc.RazorPages;
using BankStatementProcessor.Data;
using BankStatementProcessor.Models;
using Microsoft.EntityFrameworkCore;

namespace BankStatementProcessor.Pages
{
    public class TransactionsModel : PageModel
    {
        private readonly ApplicationDbContext _context;

        public List<Transaction> Transactions { get; set; }

        public TransactionsModel(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task OnGetAsync()
        {
            Transactions = await _context.Transactions.ToListAsync();
        }
    }
}
