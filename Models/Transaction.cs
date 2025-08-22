using System;
using System.ComponentModel.DataAnnotations;

namespace BankStatementProcessor.Models
{
    public class Transaction
    {
        public int Id { get; set; }
        public required DateTime Fecha { get; set; }
        public required string Comentarios { get; set; }
        public required decimal Monto { get; set; }
        public required decimal Balance { get; set; }
        public required string Cheque { get; set; }
    }
}
