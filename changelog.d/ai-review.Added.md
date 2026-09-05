- **`/ai-review` on a pull request asks an OpenAI model for a second opinion.** Manual only, for the
  same reason #467 turned off automatic CodeRabbit reviews: a review on every push spends an
  allowance on pull requests nobody was waiting for. The model answers six fixed questions about
  route inventories, tenancy, migrations, untrusted input, and locking, each answer needing a file
  and a line, then lists anything else that would break in production. Only people with write access
  can trigger it, so a drive-by comment on a fork's pull request cannot spend the tokens. It needs an
  `OPENAI_API_KEY` repository secret; the model id comes from an optional `OPENAI_MODEL` repository
  variable.
