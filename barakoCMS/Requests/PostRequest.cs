using barakoCMS.Models;
using MediatR;

namespace barakoCMS.Requests {

	public class GetAllPostsRequest : IRequest<GetAllPostsResponse> {
		public int PageNumber { get; set; } = 1;
		public int PageSize { get; set; } = 10;
		public string SearchTerm { get; set; }
		}

	public class GetAllPostsResponse {
		public List<Post> Posts { get; set; }
		}

	public class GetPostByIdRequest : IRequest<GetPostByIdResponse> {
		public Guid Id { get; set; }
		}

	public class GetPostByIdResponse {
		public Post Post { get; set; }
		}

	public class CreatePostRequest : IRequest<CreatePostResponse> {
		public string Title { get; set; }
		public string Content { get; set; }
		public string AuthorId { get; set; }
		}

	public class CreatePostResponse {
		public Post Post { get; set; }
		}

	public class UpdatePostRequest : IRequest<UpdatePostResponse> {
		public Guid Id { get; set; }
		public string Title { get; set; }
		public string Content { get; set; }
		public string UserId { get; set; }
		}

	public class UpdatePostResponse {
		public Post Post { get; set; }
		}

	public class DeletePostRequest : IRequest<DeletePostResponse> {
		public Guid Id { get; set; }
		public string UserId { get; set; }
		}

	public class DeletePostResponse {
		public bool Success { get; set; }
		}
	}