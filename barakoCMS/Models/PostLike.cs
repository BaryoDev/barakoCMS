using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Models {
	public class PostLike {
		public Guid PostId { get; set; }
		public virtual Post Post { get; set; }
		public string UserId { get; set; }
		public virtual IdentityUser User { get; set; }
		}

	}
