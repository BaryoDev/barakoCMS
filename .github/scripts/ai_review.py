#!/usr/bin/env python3
"""Ask an OpenAI model to review one pull request, and post what it says as a comment.

Called only by .github/workflows/ai-review.yml, which is triggered by hand. See that file for why
this is not automatic.

The six questions below are the ones a human reviewer on this repository actually asks. They are
deliberately narrow: a model given "review this code" returns style opinions, and style is already
settled by the analyzers. Given a fixed checklist it either answers a question or says it cannot,
and an answer with a line number is one somebody can check.

Every provider worth using speaks the OpenAI chat-completions shape, so the endpoint is a setting
rather than a constant. Point it at whichever one has quota.

Environment:
  OPENAI_API_KEY   required, from the repository secret of the same name
  OPENAI_MODEL     model id, defaults to the balanced OpenAI tier
  OPENAI_BASE_URL  API root, defaults to OpenAI. Set it to use a different provider, for example
                   https://generativelanguage.googleapis.com/v1beta/openai for Gemini
  PR_DIFF_FILE     path to the unified diff to review
  PR_NUMBER        the pull request being reviewed
  GITHUB_REPOSITORY, GITHUB_TOKEN  for posting the comment
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request

# A diff larger than this is truncated rather than refused. A partial review of a big pull request
# is still worth having, and refusing outright means the biggest changes get the least attention.
MAX_DIFF_CHARS = 220_000

QUESTIONS = """1. Does every new route appear in the route inventory tests, with the capability gate the issue asked for?
2. Does every Marten session and every raw SQL string name its tenant?
3. Does every new document, event or index have a migration or an upcaster?
4. Does anything taken from an exception, a request or a user-supplied string reach a log or a response without being examined first?
5. Does any read-then-write on a shared row, or any outbound call to a URL that is not a constant, name its lock or its bound?
6. Do the new tests actually depend on the new production code, or would they pass with that code reverted?"""

SYSTEM = """You review pull requests for barakoCMS, a headless CMS on .NET 10, Marten and Postgres, using FastEndpoints and xunit v3. It is multi-tenant with conjoined tenancy.

Answer the six questions you are given, in order, about the diff you are shown. For each one:

- Answer yes, no, or "not reachable from this diff".
- Give the file and line that proves your answer. No line, no finding.
- Say nothing about formatting, naming or style. Analyzers own those.

Then, under a heading "Other", list anything that would break in production, at most five items, each with a file and line and a concrete failure: the input or state, and the wrong output or crash it causes. If you have none, write "Nothing else found." Do not pad the list.

Be brief. A reviewer reads this next to the diff, not instead of it. Do not restate what the diff does.

Write plainly. Never use an em dash or an en dash; use a comma, a full stop or brackets."""


def die(msg: str) -> None:
    print(f"ai-review: {msg}", file=sys.stderr)
    sys.exit(1)


def post_json(url: str, payload: dict, headers: dict, model_hint: str = "that model") -> dict:
    body = json.dumps(payload).encode("utf-8")
    host = url.split("/")[2]

    # A free tier answers 503 "high demand" often enough that one attempt is not a fair test of
    # whether a review is possible. Overloaded and rate limited both clear on their own; no quota
    # does not, so that one is not retried.
    delays = [5, 15, 40, 90]
    for attempt in range(len(delays) + 1):
        req = urllib.request.Request(url, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=600) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:800]
            low = detail.lower()

            # A 429 normally means "slow down", so it reads as something a retry fixes. When it
            # carries a quota message it means the opposite, and no retry will ever clear it.
            if e.code == 429 and ("credit" in low or "quota" in low):
                die(
                    "the account has no quota left, so no review ran. This is not a rate limit and "
                    "retrying will not help. On OpenAI the daily free allowance only exists once "
                    "data sharing is turned on for that project; otherwise the project needs "
                    "credits. Or set OPENAI_BASE_URL to a provider that has quota."
                )

            # The codex models are the obvious pick for reviewing code and they are the ones that
            # do not work here: they are served by /v1/responses, not by chat completions.
            if e.code == 404 and "v1/responses" in detail:
                die(
                    f"{model_hint} is not served by the chat completions endpoint. Set "
                    "OPENAI_MODEL to a model that is."
                )

            retryable = e.code in (429, 500, 502, 503, 504)
            if retryable and attempt < len(delays):
                wait = delays[attempt]
                print(
                    f"ai-review: {host} returned {e.code}, waiting {wait}s and trying again "
                    f"({attempt + 1} of {len(delays)})",
                    file=sys.stderr,
                )
                time.sleep(wait)
                continue

            # The key is in a header, never in the body, so this cannot print it.
            if retryable:
                die(
                    f"{host} returned {e.code} on every attempt. {model_hint} is busy; try again "
                    f"later or set OPENAI_MODEL to another one. Last body: {detail}"
                )
            die(f"{host} returned {e.code}: {detail}")
        except urllib.error.URLError as e:
            if attempt < len(delays):
                time.sleep(delays[attempt])
                continue
            die(f"could not reach {host}: {e.reason}")


def main() -> None:
    api_key = os.environ.get("OPENAI_API_KEY", "")
    if not api_key:
        die("OPENAI_API_KEY is not set. Add it with: gh secret set OPENAI_API_KEY --repo <owner>/<repo>")

    model = os.environ.get("OPENAI_MODEL") or "gpt-5.6-terra"
    base_url = (os.environ.get("OPENAI_BASE_URL") or "https://api.openai.com/v1").rstrip("/")
    pr_number = os.environ.get("PR_NUMBER") or die("PR_NUMBER is not set")
    repo = os.environ.get("GITHUB_REPOSITORY") or die("GITHUB_REPOSITORY is not set")
    gh_token = os.environ.get("GITHUB_TOKEN") or die("GITHUB_TOKEN is not set")

    diff_file = os.environ.get("PR_DIFF_FILE") or die("PR_DIFF_FILE is not set")
    with open(diff_file, encoding="utf-8", errors="replace") as fh:
        diff = fh.read()

    if not diff.strip():
        die("the diff is empty, nothing to review")

    truncated = len(diff) > MAX_DIFF_CHARS
    if truncated:
        diff = diff[:MAX_DIFF_CHARS]

    user = f"Questions:\n\n{QUESTIONS}\n\nDiff:\n\n```diff\n{diff}\n```"
    if truncated:
        user += (
            f"\n\nThis diff was cut off at {MAX_DIFF_CHARS} characters. Review what you were given "
            "and say at the end that you did not see all of it."
        )

    result = post_json(
        f"{base_url}/chat/completions",
        {
            "model": model,
            "messages": [
                {"role": "system", "content": SYSTEM},
                {"role": "user", "content": user},
            ],
        },
        {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        model_hint=model,
    )

    try:
        review = result["choices"][0]["message"]["content"].strip()
    except (KeyError, IndexError):
        die(f"unexpected response shape: {json.dumps(result)[:400]}")

    # The prompt asks for no dashes and a model agrees and then uses them anyway. This comment goes
    # out under the repository's name, and house style here is that those dashes do not appear in
    # it, so the rule is enforced here rather than requested.
    for dash, plain in (("\u2014", ", "), ("\u2013", ", "), ("&mdash;", ", "), ("&ndash;", ", ")):
        review = review.replace(dash, plain)

    usage = result.get("usage", {})
    spent = ""
    if usage:
        spent = (
            f"\n\n<sub>{model} &middot; {usage.get('prompt_tokens', '?')} in, "
            f"{usage.get('completion_tokens', '?')} out</sub>"
        )

    comment = (
        "## Review, on request\n\n"
        "Asked for by hand, not on every push. Treat it as a second opinion: it has the diff and "
        "nothing else, so it cannot see the rest of the codebase and it is wrong sometimes.\n\n"
        f"{review}{spent}"
    )

    post_json(
        f"https://api.github.com/repos/{repo}/issues/{pr_number}/comments",
        {"body": comment},
        {
            "Authorization": f"Bearer {gh_token}",
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/json",
            "User-Agent": "barakocms-ai-review",
        },
    )
    print(f"ai-review: posted a review of #{pr_number} using {model}")


if __name__ == "__main__":
    main()
