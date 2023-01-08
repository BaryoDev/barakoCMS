using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations.Schema;

namespace barakoCMS.Models {

	public class Post {

		[DatabaseGenerated(DatabaseGeneratedOption.Identity)]
		public Guid Id { get; set; }

		public string Title { get; set; }
		public string Content { get; set; }
		public string AuthorId { get; set; }
		public virtual IdentityUser Author { get; set; }
		public DateTime Created { get; set; }
		public DateTime Updated { get; set; }
		}
	}