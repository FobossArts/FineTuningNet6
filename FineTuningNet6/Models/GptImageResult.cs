using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FineTuningNet6.Models
{
    [Table("GptImageResult")]
    public class GptImageResult
    {
        public int Id { get; set; }
        public string FileName { get; set; } = default!;
        public string ApiResponse { get; set; } = default!;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
