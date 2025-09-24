# pr-bot/review.py
import os, json, requests, textwrap
from typing import List, Dict
from openai import AzureOpenAI

GITHUB_API = "https://api.github.com"

REPO = os.getenv("REPO")
PR_NUMBER = os.getenv("PR_NUMBER")
TOKEN = os.getenv("GITHUB_TOKEN")
RUN_MODE = os.getenv("RUN_MODE", "summary").lower()  # "summary" | "inline"

AZURE_OPENAI_API_KEY = os.getenv("AZURE_OPENAI_API_KEY")
AZURE_OPENAI_ENDPOINT = os.getenv("AZURE_OPENAI_ENDPOINT")
AZURE_OPENAI_API_VERSION = os.getenv("AZURE_OPENAI_API_VERSION", "2025-01-01-preview")
AZURE_OPENAI_CHAT_DEPLOYMENT_NAME = os.getenv("AZURE_OPENAI_CHAT_DEPLOYMENT_NAME", "gpt-4o-mini")

HEADERS = {
    "Authorization": f"Bearer {TOKEN}",
    "Accept": "application/vnd.github+json"
}

PROMPT_PATH = "prompts/azure_cost_review.md"

def gh_get(path, params=None):
    r = requests.get(f"{GITHUB_API}{path}", headers=HEADERS, params=params)
    r.raise_for_status()
    return r.json()

def gh_post(path, payload):
    r = requests.post(f"{GITHUB_API}{path}", headers=HEADERS, data=json.dumps(payload))
    r.raise_for_status()
    return r.json()

def get_pr_files() -> List[Dict]:
    files = []
    page = 1
    while True:
        batch = gh_get(f"/repos/{REPO}/pulls/{PR_NUMBER}/files", params={"per_page": 100, "page": page})
        files.extend(batch)
        if len(batch) < 100:
            break
        page += 1
    return files

def load_prompt() -> str:
    with open(PROMPT_PATH, "r", encoding="utf-8") as f:
        return f.read()

def build_model_input(files: List[Dict]) -> str:
    base = load_prompt()
    parts = [base.strip(), "\n\n=== CODE DIFF (with limited context) ===\n"]
    for f in files:
        filename = f["filename"]
        patch = f.get("patch") or ""
        if not patch:
            continue
        if len(patch) > 30_000:
            patch = patch[:30_000] + "\n... [patch truncated]"
        parts.append(f"\n# File: {filename}\n{patch}")
    return "\n".join(parts)

def initialize_llm() -> AzureOpenAI:
    if not (AZURE_OPENAI_API_KEY and AZURE_OPENAI_ENDPOINT):
        raise RuntimeError("Azure OpenAI env vars missing. Set AZURE_OPENAI_API_KEY and AZURE_OPENAI_ENDPOINT.")
    return AzureOpenAI(
        api_key=AZURE_OPENAI_API_KEY,
        azure_endpoint=AZURE_OPENAI_ENDPOINT,
        api_version=AZURE_OPENAI_API_VERSION,
    )

def call_model(prompt: str) -> str:
    """
    Returns either:
      - Markdown (summary mode), or
      - JSON string: [{"file":"path/file.cs","line":123,"body":"..."}] for inline mode.
    """
    client = initialize_llm()

    # System message + output contract to reduce hallucination
    inline_contract = textwrap.dedent("""
      If (and only if) you are asked to produce inline comments, return a pure JSON array:
      [
        {"file": "<relative file path from repo root>", "line": <int line number on new code>, "body": "<short actionable comment>"},
        ...
      ]
      Do not wrap in markdown. Do not include extra keys. Keep bodies concise and cost-focused.
    """).strip()

    summary_contract = textwrap.dedent("""
      If producing a summary, write concise markdown with sections per issue:
      - Problem
      - Cost impact (specific to Azure services, e.g., Cosmos RU, Blob ops, Functions executions, App Insights ingestion)
      - Recommended fix (APIs, batching, patch, retries/backoff, sampling, etc.)
      - Reference (brief best-practice note)
    """).strip()

    mode_hint = "Produce inline JSON comments only." if RUN_MODE == "inline" else "Produce a single concise markdown summary comment."
    content = f"{mode_hint}\n\n{summary_contract}\n\n{inline_contract}\n\n---\n{prompt}"

    resp = client.chat.completions.create(
        model=AZURE_OPENAI_CHAT_DEPLOYMENT_NAME,
        messages=[
            {"role": "system", "content": "You are a senior Azure cost optimization reviewer. Be precise, cost-focused, and practical."},
            {"role": "user", "content": content}
        ],
        temperature=0.2,
    )

    return resp.choices[0].message.content.strip()

def post_summary_comment(body_md: str):
    gh_post(f"/repos/{REPO}/issues/{PR_NUMBER}/comments", {"body": body_md})

def post_inline_review(comments: List[Dict]):
    review_comments = []
    for c in comments:
        if not all(k in c for k in ("file", "line", "body")):
            continue
        review_comments.append({
            "path": c["file"],
            "line": int(c["line"]),
            "side": "RIGHT",
            "body": c["body"]
        })

    if not review_comments:
        post_summary_comment("> Inline mapping failed, posting summary instead.\n\n" +
                             "• " + "\n• ".join([c.get("body","") for c in comments if c.get("body")]))
        return

    payload = {
        "event": "COMMENT",
        "comments": review_comments
    }
    gh_post(f"/repos/{REPO}/pulls/{PR_NUMBER}/reviews", payload)

def main():
    files = get_pr_files()
    model_input = build_model_input(files)
    analysis = call_model(model_input)

    # Try inline JSON if RUN_MODE=inline
    if RUN_MODE == "inline":
        try:
            parsed = json.loads(analysis)
            if isinstance(parsed, list):
                post_inline_review(parsed)
                return
        except json.JSONDecodeError:
            # fall back to summary
            pass

    # Otherwise summary markdown
    post_summary_comment(analysis)

if __name__ == "__main__":
    main()

