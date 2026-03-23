#!/usr/bin/env python3
"""
Code Assistant - CLI + Plan Executor + GUI Diff Reviewer (Dark Theme)

- Uses a custom chat endpoint: http(s)://<host>/v1/chat  (NOT Ollama)
- Keeps the same request format style as before:
    payload = {
      "model": model,
      "messages": history,
      "options": {"max_tokens": ..., "temperature": ...},
      "stream": False
    }

Features:
- Chat
- Web search (DuckDuckGo HTML)
- Read/edit files (with GUI diff hunks accept/reject)
- Run commands (whitelist safety)
- Plan mode (JSON steps) with GUI diff for write_code/edit_file steps

Notes:
- GUI diff uses Tkinter. If Tkinter isn't available, falls back to simple console confirmation.
"""

import requests
import re
import json
import sys
import os
import difflib
import subprocess
import chardet
from pathlib import Path
from bs4 import BeautifulSoup
from urllib.parse import quote_plus
from shutil import copyfile
from colorama import init, Fore, Style

# Initialize colorama for cross-platform color support
init()

# =========================
# Configuration (IMPORTANT)
# =========================

# Your only available endpoint (user said: xxx.xxx.xxx.xxx/v1/chat api)
# You can override via env: CODE_ASSISTANT_CHAT_API_URL
CHAT_API_URL = os.environ.get("CODE_ASSISTANT_CHAT_API_URL", "http://xxx.xxx.xxx.xxx/v1/chat")

# Model name may be required by your server; override via env: CODE_ASSISTANT_MODEL
DEFAULT_MODEL = os.environ.get("CODE_ASSISTANT_MODEL", "default")
CURRENT_MODEL = DEFAULT_MODEL

MAX_SEARCH_RESULTS = 5
MAX_URL_CONTENT_LENGTH = 10000

SHOW_THINKING = False
MAX_THINKING_LENGTH = 5000

DEFAULT_TIMEOUT = 500
WORKING_DIRECTORY = None

# Command execution safety (whitelist)
SAFE_COMMAND_PREFIXES = [
    "python", "python3", "node", "npm", "git", "ls", "dir", "cd", "type", "cat", "make",
    "dotnet", "gradle", "mvn", "cargo", "rustc", "go", "test", "echo"
]
DANGEROUS_COMMANDS = ["rm", "del", "sudo", "chmod", "chown", "mv", "cp", "rmdir", "rd", "format", "mkfs", "dd", ">", ">>"]


# =========================
# GUI Diff (Dark + Colored)
# =========================

def gui_apply_changes_by_hunk(old_text: str, new_text: str, file_path: str, ctx_lines: int = 3):
    """
    Tkinter UI로 diff(hunk) 단위 accept/reject.
    - 다크 테마 + 색상 diff (+/-/context)
    - hunk 단위 Accept/Reject + File 단위 Accept/Reject + Apply/Cancel

    Return:
      - merged_text (str)
      - applied (bool)
    """
    try:
        import tkinter as tk
        from tkinter import ttk, messagebox
    except Exception:
        print(f"{Fore.YELLOW}Tkinter not available. Falling back to console confirm for: {file_path}{Style.RESET_ALL}")
        if old_text == new_text:
            return old_text, False
        print(f"{Fore.CYAN}Proposed change for {file_path}. Apply? (y/n){Style.RESET_ALL}")
        ans = input("> ").strip().lower()
        return (new_text, True) if ans in ("y", "yes") else (old_text, False)

    import difflib

    # ---------- Dark theme palette ----------
    BG = "#1e1e1e"
    BG2 = "#252526"
    FG = "#d4d4d4"
    MUTED = "#9da0a6"
    ACCENT = "#0e639c"
    BORDER = "#3c3c3c"
    ADD = "#6A9955"     # green
    DEL = "#F14C4C"     # red
    WARN = "#DCDCAA"    # yellow
    HDR = "#4FC1FF"     # cyan-ish

    def apply_dark_ttk_style(root):
        style = ttk.Style(root)
        try:
            style.theme_use("clam")
        except Exception:
            pass

        style.configure(".", background=BG, foreground=FG, fieldbackground=BG2)
        style.configure("TFrame", background=BG)
        style.configure("TLabel", background=BG, foreground=FG)
        style.configure("TLabelframe", background=BG, foreground=FG)
        style.configure("TLabelframe.Label", background=BG, foreground=FG)

        style.configure(
            "TButton",
            background=BG2,
            foreground=FG,
            bordercolor=BORDER,
            focusthickness=2,
            focuscolor=ACCENT,
            padding=(10, 6),
        )
        style.map(
            "TButton",
            background=[("active", "#2d2d30"), ("pressed", "#3a3a3d")],
            foreground=[("disabled", MUTED)],
        )

        style.configure("TSeparator", background=BORDER)
        style.configure("TPanedwindow", background=BG)

    def configure_dark_text(widget):
        widget.configure(
            background=BG2,
            foreground=FG,
            insertbackground=FG,
            selectbackground=ACCENT,
            selectforeground=FG,
            relief="flat",
            borderwidth=1,
            highlightthickness=1,
            highlightbackground=BORDER,
            highlightcolor=ACCENT,
        )
        widget.tag_configure("add", foreground=ADD)
        widget.tag_configure("del", foreground=DEL)
        widget.tag_configure("ctx", foreground=MUTED)
        widget.tag_configure("hdr", foreground=HDR)
        widget.tag_configure("warn", foreground=WARN)

    def configure_dark_listbox(lb):
        lb.configure(
            background=BG2,
            foreground=FG,
            selectbackground=ACCENT,
            selectforeground=FG,
            highlightthickness=1,
            highlightbackground=BORDER,
            highlightcolor=ACCENT,
            relief="flat",
            borderwidth=1,
        )

    # ---------- Build hunks ----------
    old_lines = old_text.splitlines(True)
    new_lines = new_text.splitlines(True)

    sm = difflib.SequenceMatcher(a=old_lines, b=new_lines)
    opcodes = sm.get_opcodes()

    hunks = []
    cur = None

    def flush():
        nonlocal cur
        if cur is not None:
            hunks.append(cur)
            cur = None

    for tag, i1, i2, j1, j2 in opcodes:
        if tag == "equal":
            flush()
            continue
        if cur is None:
            cur = {"i1": i1, "i2": i2, "j1": j1, "j2": j2}
        else:
            cur["i2"] = i2
            cur["j2"] = j2
    flush()

    if not hunks:
        return old_text, False

    # None/PENDING, True/ACCEPT, False/REJECT
    decisions = [None] * len(hunks)

    def build_merged_text():
        out = []
        old_pos = 0
        for idx, h in enumerate(hunks):
            i1, i2, j1, j2 = h["i1"], h["i2"], h["j1"], h["j2"]
            out.extend(old_lines[old_pos:i1])

            d = decisions[idx]
            if d is True:
                out.extend(new_lines[j1:j2])
            else:
                out.extend(old_lines[i1:i2])

            old_pos = i2

        out.extend(old_lines[old_pos:])
        return "".join(out)

    def status_icon(i):
        d = decisions[i]
        if d is True:
            return "✅"
        if d is False:
            return "❌"
        return "⏳"

    def hunk_title(i):
        h = hunks[i]
        return f"{status_icon(i)} Hunk {i+1}: old({h['i1']+1}-{h['i2']}) → new({h['j1']+1}-{h['j2']})"

    def unified_diff_for_hunk(i):
        h = hunks[i]
        i1, i2, j1, j2 = h["i1"], h["i2"], h["j1"], h["j2"]

        old_start = max(0, i1 - ctx_lines)
        old_end = min(len(old_lines), i2 + ctx_lines)
        new_start = max(0, j1 - ctx_lines)
        new_end = min(len(new_lines), j2 + ctx_lines)

        a = old_lines[old_start:old_end]
        b = new_lines[new_start:new_end]

        diff = list(difflib.unified_diff(
            a, b,
            fromfile="old",
            tofile="new",
            lineterm=""
        ))
        return diff

    def render_diff(text_widget, diff_lines):
        text_widget.configure(state="normal")
        text_widget.delete("1.0", tk.END)

        if not diff_lines:
            text_widget.insert(tk.END, "(no diff)\n", "ctx")
            text_widget.configure(state="disabled")
            return

        for ln in diff_lines:
            if ln.startswith(("---", "+++")):
                text_widget.insert(tk.END, ln + "\n", "hdr")
            elif ln.startswith("@@"):
                text_widget.insert(tk.END, ln + "\n", "hdr")
            elif ln.startswith("+") and not ln.startswith("+++"):
                text_widget.insert(tk.END, ln + "\n", "add")
            elif ln.startswith("-") and not ln.startswith("---"):
                text_widget.insert(tk.END, ln + "\n", "del")
            else:
                text_widget.insert(tk.END, ln + "\n", "ctx")

        text_widget.configure(state="disabled")

    # ---------- GUI ----------
    root = tk.Tk()
    root.title(f"Diff Review - {file_path}")
    root.geometry("1400x860")
    root.configure(background=BG)
    apply_dark_ttk_style(root)

    top = ttk.Frame(root)
    top.pack(fill="x", padx=10, pady=8)

    title = ttk.Label(top, text=f"File: {file_path}", font=("Segoe UI", 12, "bold"))
    title.pack(side="left")

    main = ttk.Panedwindow(root, orient="horizontal")
    main.pack(fill="both", expand=True, padx=10, pady=8)

    left = ttk.Frame(main, width=430)
    right = ttk.Frame(main)
    main.add(left, weight=1)
    main.add(right, weight=4)

    ttk.Label(left, text="Hunks", font=("Segoe UI", 10, "bold")).pack(anchor="w")
    hunk_list = tk.Listbox(left, height=25)
    configure_dark_listbox(hunk_list)
    hunk_list.pack(fill="both", expand=True, pady=(6, 10))

    btns = ttk.Frame(left)
    btns.pack(fill="x")

    def refresh_hunk_list(select_idx=None):
        hunk_list.delete(0, tk.END)
        for i in range(len(hunks)):
            hunk_list.insert(tk.END, hunk_title(i))
        if select_idx is not None and 0 <= select_idx < len(hunks):
            hunk_list.selection_clear(0, tk.END)
            hunk_list.selection_set(select_idx)
            hunk_list.activate(select_idx)

    def accept_selected():
        sel = hunk_list.curselection()
        if not sel:
            return
        i = sel[0]
        decisions[i] = True
        refresh_hunk_list(select_idx=i)
        update_panels()

    def reject_selected():
        sel = hunk_list.curselection()
        if not sel:
            return
        i = sel[0]
        decisions[i] = False
        refresh_hunk_list(select_idx=i)
        update_panels()

    def accept_all():
        for i in range(len(decisions)):
            decisions[i] = True
        refresh_hunk_list(select_idx=0)
        update_panels()

    def reject_all():
        for i in range(len(decisions)):
            decisions[i] = False
        refresh_hunk_list(select_idx=0)
        update_panels()

    ttk.Button(btns, text="✓ Accept Hunk", command=accept_selected).pack(fill="x", pady=3)
    ttk.Button(btns, text="✗ Reject Hunk", command=reject_selected).pack(fill="x", pady=3)
    ttk.Separator(btns).pack(fill="x", pady=10)
    ttk.Button(btns, text="⇧ Accept File (All)", command=accept_all).pack(fill="x", pady=3)
    ttk.Button(btns, text="⇩ Reject File (All)", command=reject_all).pack(fill="x", pady=3)

    ttk.Label(right, text="Selected Hunk Diff (Unified)", font=("Segoe UI", 10, "bold")).pack(anchor="w")
    diff_text = tk.Text(right, wrap="none", height=18)
    configure_dark_text(diff_text)
    diff_text.pack(fill="both", expand=False, pady=(6, 14))

    ttk.Label(right, text="Preview (Merged Result)", font=("Segoe UI", 10, "bold")).pack(anchor="w")
    preview_text = tk.Text(right, wrap="none")
    configure_dark_text(preview_text)
    preview_text.pack(fill="both", expand=True, pady=(6, 6))

    bottom = ttk.Frame(root)
    bottom.pack(fill="x", padx=10, pady=10)

    applied_flag = {"applied": False}

    def on_apply():
        # pending -> reject (conservative)
        if any(d is None for d in decisions):
            from tkinter import messagebox
            if not messagebox.askyesno(
                "Pending hunks",
                "Some hunks are still ⏳ PENDING.\nThey will be treated as ❌ REJECT.\nApply anyway?"
            ):
                return
            for i in range(len(decisions)):
                if decisions[i] is None:
                    decisions[i] = False
        applied_flag["applied"] = True
        root.destroy()

    def on_cancel():
        applied_flag["applied"] = False
        root.destroy()

    ttk.Button(bottom, text="Cancel", command=on_cancel).pack(side="right", padx=6)
    ttk.Button(bottom, text="Apply", command=on_apply).pack(side="right", padx=6)

    def update_panels():
        sel = hunk_list.curselection()
        idx = sel[0] if sel else 0
        diff_lines = unified_diff_for_hunk(idx)
        render_diff(diff_text, diff_lines)

        merged = build_merged_text()
        preview_text.configure(state="normal")
        preview_text.delete("1.0", tk.END)
        preview_text.insert(tk.END, merged, "ctx")
        preview_text.configure(state="disabled")
        try:
            line_to_see = hunks[idx]["i1"] + 1
            preview_text.see(f"{line_to_see}.0")
        except Exception:
            pass

    def on_select(_evt=None):
        update_panels()

    hunk_list.bind("<<ListboxSelect>>", on_select)

    refresh_hunk_list(select_idx=0)
    update_panels()
    root.mainloop()

    if not applied_flag["applied"]:
        return old_text, False

    return build_merged_text(), True


# =========================
# Encoding / File IO
# =========================

def detect_file_encoding(file_path):
    try:
        with open(file_path, 'rb') as f:
            raw = f.read(32)
            if not raw:
                return 'utf-8', False
            if raw.startswith(b'\xef\xbb\xbf'):
                return 'utf-8-sig', True
            elif raw.startswith(b'\xff\xfe\x00\x00'):
                return 'utf-32-le', True
            elif raw.startswith(b'\x00\x00\xfe\xff'):
                return 'utf-32-be', True
            elif raw.startswith(b'\xff\xfe'):
                return 'utf-16-le', True
            elif raw.startswith(b'\xfe\xff'):
                return 'utf-16-be', True

            f.seek(0)
            raw_data = f.read(4096)
            try:
                result = chardet.detect(raw_data)
                encoding = result.get('encoding')
                confidence = result.get('confidence', 0.0)
                if encoding:
                    if confidence < 0.7:
                        print(f"Warning: Low confidence ({confidence:.2f}) encoding detection: {encoding}")
                    return encoding, False
            except Exception:
                pass
    except Exception as e:
        print(f"Warning: Error detecting encoding: {e}")

    return 'utf-8', False


def read_file_content(file_path, start_line=None, end_line=None):
    try:
        if WORKING_DIRECTORY and not os.path.isabs(file_path):
            file_path = os.path.join(WORKING_DIRECTORY, file_path)
        elif WORKING_DIRECTORY and os.path.isabs(file_path):
            if not os.path.abspath(file_path).startswith(WORKING_DIRECTORY):
                print(f"{Fore.RED}Error: File '{file_path}' is outside working directory. Access denied.{Style.RESET_ALL}")
                return None

        if not os.path.exists(file_path):
            print(f"{Fore.RED}Error: File '{file_path}' not found.{Style.RESET_ALL}")
            return None

        if not os.access(file_path, os.R_OK):
            print(f"{Fore.RED}Error: File '{file_path}' is not readable.{Style.RESET_ALL}")
            return None

        encoding, _ = detect_file_encoding(file_path)
        with open(file_path, 'r', encoding=encoding) as file:
            if start_line is None and end_line is None:
                content = file.read()
            else:
                lines = file.readlines()
                total = len(lines)
                start_idx = max(0, (start_line or 1) - 1)
                end_idx = min(total, end_line) if end_line is not None else total
                selected = lines[start_idx:end_idx]
                content = ''.join(selected)
                content = f"--- Lines {start_idx+1}-{end_idx} of {total} ---\n{content}"

        if encoding.lower() not in ['utf-8', 'utf-8-sig', 'ascii']:
            print(f"{Fore.CYAN}Note: Read '{file_path}' with {encoding} encoding.{Style.RESET_ALL}")

        return content
    except Exception as e:
        print(f"{Fore.RED}Error reading '{file_path}': {e}{Style.RESET_ALL}")
        return None


def write_file_content(file_path, content, create_backup=True):
    try:
        if WORKING_DIRECTORY and not os.path.isabs(file_path):
            file_path = os.path.join(WORKING_DIRECTORY, file_path)
        elif WORKING_DIRECTORY and os.path.isabs(file_path):
            if not os.path.abspath(file_path).startswith(WORKING_DIRECTORY):
                print(f"{Fore.RED}Error: Cannot write outside working directory: {file_path}{Style.RESET_ALL}")
                return False

        parent_dir = os.path.dirname(file_path)
        if parent_dir and not os.path.exists(parent_dir):
            os.makedirs(parent_dir)
            print(f"{Fore.GREEN}Created directory: {parent_dir}{Style.RESET_ALL}")

        original_encoding = 'utf-8'
        if os.path.exists(file_path):
            original_encoding, _ = detect_file_encoding(file_path)
            if create_backup:
                try:
                    copyfile(file_path, f"{file_path}.bak")
                    print(f"{Fore.CYAN}Backup created: {file_path}.bak{Style.RESET_ALL}")
                except Exception as e:
                    print(f"{Fore.YELLOW}Warning: Could not create backup: {e}{Style.RESET_ALL}")

        with open(file_path, 'w', encoding=original_encoding) as f:
            f.write(content)

        if original_encoding.lower() not in ['utf-8', 'utf-8-sig', 'ascii']:
            print(f"{Fore.CYAN}Note: Wrote '{file_path}' with {original_encoding} encoding.{Style.RESET_ALL}")

        return True
    except Exception as e:
        print(f"{Fore.RED}Error writing '{file_path}': {e}{Style.RESET_ALL}")
        return False


def generate_colored_diff(original, modified, file_path):
    diff = difflib.unified_diff(
        original.splitlines(),
        modified.splitlines(),
        fromfile=f'a/{file_path}',
        tofile=f'b/{file_path}',
        lineterm=''
    )
    colored = []
    for line in diff:
        if line.startswith('+') and not line.startswith('+++'):
            colored.append(f"{Fore.GREEN}{line}{Style.RESET_ALL}")
        elif line.startswith('-') and not line.startswith('---'):
            colored.append(f"{Fore.RED}{line}{Style.RESET_ALL}")
        elif line.startswith('@'):
            colored.append(f"{Fore.CYAN}{line}{Style.RESET_ALL}")
        else:
            colored.append(line)
    return '\n'.join(colored)


# =========================
# Web search + URL fetch
# =========================

def fetch_url_content(url):
    try:
        if not url.startswith(('http://', 'https://')):
            url = 'https://' + url

        headers = {'User-Agent': 'Mozilla/5.0'}
        resp = requests.get(url, headers=headers, timeout=10)

        if resp.status_code != 200:
            return f"Failed to fetch {url}: HTTP {resp.status_code}"

        ctype = (resp.headers.get('Content-Type') or '').lower()
        if 'text/html' in ctype:
            soup = BeautifulSoup(resp.text, 'html.parser')
            for s in soup(["script", "style"]):
                s.extract()
            text = soup.get_text(separator='\n')
            lines = (ln.strip() for ln in text.splitlines())
            text = '\n'.join(ln for ln in lines if ln)
        else:
            text = resp.text

        if len(text) > MAX_URL_CONTENT_LENGTH:
            text = text[:MAX_URL_CONTENT_LENGTH] + "... [truncated]"
        return text
    except Exception as e:
        return f"Failed to fetch {url}: {e}"


def duckduckgo_search(query, num_results=MAX_SEARCH_RESULTS):
    print(f"Searching the web for: {query}")
    try:
        search_url = f"https://html.duckduckgo.com/html/?q={quote_plus(query)}"
        headers = {'User-Agent': 'Mozilla/5.0'}
        resp = requests.get(search_url, headers=headers, timeout=10)
        if resp.status_code != 200:
            return []

        soup = BeautifulSoup(resp.text, 'html.parser')
        results = []
        for result in soup.select('.result'):
            title_elem = result.select_one('.result__title')
            snippet_elem = result.select_one('.result__snippet')
            url_elem = result.select_one('.result__url')
            if title_elem and snippet_elem:
                title = title_elem.get_text().strip()
                snippet = snippet_elem.get_text().strip()
                url = url_elem.get_text().strip() if url_elem else ""
                results.append({"title": title, "snippet": snippet, "url": url})
                if len(results) >= num_results:
                    break
        return results
    except Exception:
        return []


# =========================
# Bracket parsing [file] / [url] / [file:10-20]
# =========================

def extract_file_paths_and_urls(query):
    pattern = r'\[((?:[^\[\]]|\[(?:[^\[\]]|\[[^\[\]]*\])*\])*)\]'
    matches = re.findall(pattern, query)

    file_paths = []
    urls = []

    for match in matches:
        if not match.strip():
            continue

        is_url = match.startswith(('http://', 'https://')) or (
            not match.startswith(('./', '../')) and
            '.' in match and '/' in match and
            ':' not in match and
            re.search(r'^[a-zA-Z0-9][a-zA-Z0-9-]*\.[a-zA-Z]{2,}', match)
        )

        if is_url:
            urls.append(match)
            continue

        if ':' in match:
            parts = match.split(':', 1)
            file_path = parts[0]
            line_range = parts[1]
            start_line = None
            end_line = None

            if '-' in line_range:
                a, b = line_range.split('-', 1)
                if a.strip():
                    try:
                        start_line = int(a.strip())
                    except ValueError:
                        pass
                if b.strip():
                    try:
                        end_line = int(b.strip())
                    except ValueError:
                        pass
            else:
                try:
                    start_line = int(line_range.strip())
                    end_line = start_line + 1
                except ValueError:
                    pass

            file_paths.append((file_path, start_line, end_line))
        else:
            file_paths.append((match, None, None))

    clean_query = query
    bracket_positions = []
    stack = []
    for i, ch in enumerate(query):
        if ch == '[':
            stack.append(i)
        elif ch == ']' and stack:
            start = stack.pop()
            bracket_positions.append((start, i + 1))

    bracket_positions.sort(reverse=True)
    for start, end in bracket_positions:
        content = query[start + 1:end - 1]
        if not content.strip():
            clean_query = clean_query[:start] + '[]' + clean_query[end:]
        else:
            clean_query = clean_query[:start] + ' ' + clean_query[end:]

    clean_query = re.sub(r'\s{2,}', ' ', clean_query).strip()
    return clean_query, file_paths, urls


# =========================
# LLM / Thinking blocks
# =========================

def process_thinking_blocks(content, chunk_size=10000):
    global SHOW_THINKING, MAX_THINKING_LENGTH
    if not content:
        return content
    if '<think>' not in content:
        return content

    if not SHOW_THINKING:
        parts = re.split(r'(<think>|</think>)', content)
        inside = False
        out = []
        for p in parts:
            if p == "<think>":
                inside = True
            elif p == "</think>":
                inside = False
            elif not inside:
                out.append(p)
        return ''.join(out)

    # SHOW_THINKING = True: show, but truncate each block
    def _truncate_block(m):
        inner = m.group(1)
        if len(inner) > MAX_THINKING_LENGTH:
            inner = inner[:MAX_THINKING_LENGTH] + f"\n... [Thinking truncated, {len(m.group(1)) - MAX_THINKING_LENGTH} more chars] ..."
        return f"<thinking>\n{inner}\n</thinking>"

    return re.sub(r'<think>(.*?)</think>', _truncate_block, content, flags=re.DOTALL)


def get_chat_response(history, model=None, timeout=None):
    """
    IMPORTANT: Keep request format consistent:
      - model
      - messages
      - options
      - stream
    """
    model_to_use = model or CURRENT_MODEL
    timeout_to_use = timeout if timeout is not None else DEFAULT_TIMEOUT

    payload = {
        "model": model_to_use,
        "messages": history,
        "options": {
            "max_tokens": 4000,
            "temperature": 0.7
        },
        "stream": False
    }

    resp = requests.post(CHAT_API_URL, json=payload, timeout=timeout_to_use)
    resp.raise_for_status()

    # Response parsing: best-effort without changing request format.
    data = resp.json()
    if isinstance(data, dict):
        if "message" in data and isinstance(data["message"], dict) and "content" in data["message"]:
            return data["message"]["content"]
        if "choices" in data and data["choices"]:
            # OpenAI-style fallback
            ch0 = data["choices"][0]
            if isinstance(ch0, dict):
                msg = ch0.get("message")
                if isinstance(msg, dict) and "content" in msg:
                    return msg["content"]
                if "text" in ch0:
                    return ch0["text"]
        if "content" in data and isinstance(data["content"], str):
            return data["content"]

    return str(data)


def get_llm_response(history, model=None, timeout=None):
    try:
        content = get_chat_response(history, model=model, timeout=timeout)
        return content
    except requests.exceptions.Timeout:
        return f"Error: Request timed out after {timeout or DEFAULT_TIMEOUT}s"
    except requests.exceptions.ConnectionError:
        return "Error: Cannot connect to chat API server."
    except requests.exceptions.HTTPError as e:
        return f"Error: HTTP {e.response.status_code}: {e.response.text}"
    except Exception as e:
        return f"Error: {e}"


# =========================
# Extract modified file content from LLM response
# =========================

def clean_explanatory_text(content):
    if not content:
        return content
    content = process_thinking_blocks(content)
    # strip outer backticks blocks if any
    if "```" in content:
        parts = content.split("```")
        if len(parts) >= 3:
            # take last code block as likely final
            block = parts[-2].lstrip()
            if "\n" in block:
                first = block.split("\n", 1)[0].strip()
                # if first line looks like language id, drop it
                if first and re.match(r'^[a-zA-Z0-9+#._-]+$', first):
                    block = block.split("\n", 1)[1] if "\n" in block else block
            content = block
    return content.strip()


def extract_modified_content(response, file_path):
    if not response:
        return None

    cleaned = response.strip()

    # Prefer code blocks
    if "```" in cleaned:
        parts = cleaned.split("```")
        code_blocks = []
        for i in range(1, len(parts), 2):
            block = parts[i].lstrip()
            if "\n" in block:
                first = block.split("\n")[0].strip()
                if first and re.match(r'^[a-zA-Z0-9+#._-]+$', first):
                    block = block.split("\n", 1)[1] if "\n" in block else block
            code_blocks.append(block)
        if code_blocks:
            return clean_explanatory_text(code_blocks[-1])

    # Otherwise treat entire response as file content
    return clean_explanatory_text(cleaned)


# =========================
# Command safety
# =========================

def is_safe_command(command):
    command = command.strip()

    for separator in [';', '&&', '||']:
        if separator in command:
            parts = [c.strip() for c in command.split(separator)]
            for i, c in enumerate(parts):
                ok, reason = is_safe_command(c)
                if not ok:
                    return False, f"Unsafe command in chain part {i+1}: {reason}"
            return True, None

    if '|' in command:
        parts = [c.strip() for c in command.split('|')]
        for i, c in enumerate(parts):
            ok, reason = is_safe_command(c)
            if not ok:
                return False, f"Unsafe command in pipe part {i+1}: {reason}"
        return True, None

    try:
        import shlex
        parts = shlex.split(command)
    except Exception:
        return False, "Could not parse command safely"

    if not parts:
        return False, "Empty command"

    base = parts[0].lower()

    if base in DANGEROUS_COMMANDS:
        return False, f"Command '{base}' is blocked"

    allowed = {
        "python", "python3", "node", "npm", "git", "ls", "dir", "cd", "pwd",
        "cat", "type", "more", "grep", "findstr", "find", "sort", "head", "tail",
        "pip", "make", "dotnet", "gradle", "mvn", "cargo", "rustc", "go",
        "test", "echo"
    }

    if base not in allowed:
        return False, f"Command '{base}' is not allowed"

    # Block python -c
    if base in ("python", "python3"):
        if any(x in parts[1:] for x in ("-c", "--command")):
            return False, "Python -c/--command is not allowed"

    # Block find -exec/-delete
    if base == "find":
        if any(x in parts[1:] for x in ("-exec", "-delete")):
            return False, "find -exec/-delete is not allowed"

    # Block dangerous git subcommands
    if base == "git" and len(parts) > 1:
        if parts[1] in ("clean", "reset", "push", "filter-branch"):
            return False, f"git {parts[1]} requires manual review"

    # Block dangerous npm subcommands
    if base == "npm" and len(parts) > 1:
        if parts[1] in ("publish", "unpublish", "login", "adduser", "access"):
            return False, f"npm {parts[1]} requires manual review"

    # Block pip uninstall
    if base == "pip" and len(parts) > 1:
        if parts[1] == "uninstall":
            return False, "pip uninstall requires manual review"

    return True, None


def execute_command(command):
    try:
        import shlex
        args = shlex.split(command)
        if not args:
            return "Error: Empty command"

        result = subprocess.run(
            args,
            shell=False,
            capture_output=True,
            text=True,
            cwd=WORKING_DIRECTORY if WORKING_DIRECTORY else None
        )

        out = ""
        if result.stdout:
            out += f"Standard Output:\n{result.stdout}\n"
        if result.stderr:
            out += f"Standard Error:\n{result.stderr}\n"
        out += f"Exit Code: {result.returncode}"
        return out
    except Exception as e:
        return f"Error executing command: {e}"


# =========================
# Query type helpers
# =========================

def is_search_query(q): return q.lower().startswith(("search:", "search "))
def is_edit_query(q): return q.lower().startswith(("edit:", "edit "))
def is_run_query(q): return q.lower().startswith(("run:", "run "))
def is_model_query(q): return q.lower().startswith(("model:", "model ", "use model:", "use model "))
def is_create_query(q): return q.lower().startswith(("create:", "create "))
def is_plan_query(q): return q.lower().startswith(("plan:", "plan ", "vibecode:", "vibecode "))

def extract_search_query(q): return q.split(":", 1)[1].strip() if q.lower().startswith("search:") else q[7:].strip() if q.lower().startswith("search ") else q
def extract_edit_query(q): return q.split(":", 1)[1].strip() if q.lower().startswith("edit:") else q[5:].strip() if q.lower().startswith("edit ") else q
def extract_run_query(q): return q.split(":", 1)[1].strip() if q.lower().startswith("run:") else q[4:].strip() if q.lower().startswith("run ") else q
def extract_create_query(q): return q.split(":", 1)[1].strip() if q.lower().startswith("create:") else q[7:].strip() if q.lower().startswith("create ") else q

def extract_model_query(q):
    s = q.strip()
    s_low = s.lower()
    if s_low.startswith("use model:"):
        s = s[10:].strip()
    elif s_low.startswith("use model "):
        s = s[10:].strip()
    elif s_low.startswith("model:"):
        s = s[6:].strip()
    elif s_low.startswith("model "):
        s = s[6:].strip()
    if (s.startswith("'") and s.endswith("'")) or (s.startswith('"') and s.endswith('"')):
        s = s[1:-1].strip()
    return s

def extract_plan_query(q):
    s = q.strip()
    s_low = s.lower()
    if s_low.startswith("plan:"):
        return s[5:].strip()
    if s_low.startswith("plan "):
        return s[5:].strip()
    if s_low.startswith("vibecode:"):
        return s[9:].strip()
    if s_low.startswith("vibecode "):
        return s[9:].strip()
    return s


# =========================
# File listing
# =========================

def get_file_list():
    try:
        files = []
        search_dir = WORKING_DIRECTORY if WORKING_DIRECTORY else '.'
        for root, dirs, filenames in os.walk(search_dir):
            for fn in filenames:
                if fn.startswith('.') or any(part.startswith('.') for part in root.split(os.sep)):
                    continue
                if WORKING_DIRECTORY:
                    rel = os.path.relpath(os.path.join(root, fn), WORKING_DIRECTORY)
                    files.append(rel)
                else:
                    files.append(os.path.join(root, fn))
        return files
    except Exception as e:
        print(f"{Fore.RED}Error listing files: {e}{Style.RESET_ALL}")
        return []


# =========================
# Handlers
# =========================

def handle_search_query(user_input, history):
    q = extract_search_query(user_input)
    print(f"{Fore.CYAN}Searching the web for: {q}{Style.RESET_ALL}")
    results = duckduckgo_search(q)

    search_content = ""
    if results:
        search_content = "\nSearch Results:\n"
        for i, r in enumerate(results, 1):
            search_content += f"{i}. {r['title']}\n   URL: {r['url']}\n   Snippet: {r['snippet']}\n\n"

    user_message = f"Web Search Query: {q}"
    if search_content:
        user_message += f"\n{search_content}"

    history.append({"role": "user", "content": user_message})

    print(f"\n{Fore.YELLOW}Thinking...{Style.RESET_ALL}\n")
    resp = get_llm_response(history)
    processed = process_thinking_blocks(resp)
    print(f"{Fore.CYAN}🤖 Assistant:{Style.RESET_ALL}\n{processed}")
    history.append({"role": "assistant", "content": resp})


def handle_regular_query(user_input, history):
    clean_query, file_items, urls = extract_file_paths_and_urls(user_input)

    files_section = ""
    if file_items:
        files_section = "\nFiles:\n"
        for fp, s, e in file_items:
            c = read_file_content(fp, s, e)
            if c:
                files_section += f"File: {fp}\nContent:\n{c}\n\n"

    url_section = ""
    if urls:
        url_section = "\nURL Content:\n"
        for u in urls:
            c = fetch_url_content(u)
            if c:
                url_section += f"URL: {u}\nContent:\n{c}\n\n"

    user_message = f"Query: {clean_query}"
    if files_section:
        user_message += files_section
    if url_section:
        user_message += url_section

    history.append({"role": "user", "content": user_message})

    print(f"{Fore.CYAN}Thinking...{Style.RESET_ALL}")
    resp = get_llm_response(history)
    processed = process_thinking_blocks(resp)
    print(f"{Fore.GREEN}{processed}{Style.RESET_ALL}")
    history.append({"role": "assistant", "content": resp})


def handle_edit_query(user_input, history):
    edit_query = extract_edit_query(user_input)
    clean_query, file_items, _ = extract_file_paths_and_urls(edit_query)

    if not file_items:
        print(f"{Fore.YELLOW}No file paths found. Use [file_path] in your edit query.{Style.RESET_ALL}")
        return

    files_content_section = "\nFiles to Edit:\n"
    for fp, s, e in file_items:
        content = read_file_content(fp, s, e)
        if content:
            files_content_section += f"File: {fp}\nContent:\n{content}\n\n"
        else:
            if not os.path.exists(os.path.join(WORKING_DIRECTORY, fp) if WORKING_DIRECTORY and not os.path.isabs(fp) else fp):
                confirm = input(f"{Fore.YELLOW}File '{fp}' doesn't exist. Create it? (y/n): {Style.RESET_ALL}").lower()
                if confirm == 'y':
                    Path(os.path.join(WORKING_DIRECTORY, fp) if WORKING_DIRECTORY and not os.path.isabs(fp) else fp).parent.mkdir(parents=True, exist_ok=True)
                    Path(os.path.join(WORKING_DIRECTORY, fp) if WORKING_DIRECTORY and not os.path.isabs(fp) else fp).touch()
                    files_content_section += f"File: {fp}\nContent:\n[New empty file]\n\n"
                    history.append({"role": "system", "content": f"Created file '{fp}'."})
                else:
                    print(f"{Fore.YELLOW}Edit cancelled for '{fp}'.{Style.RESET_ALL}")
                    return

    user_message = f"Edit Request: {clean_query}{files_content_section}"
    history.append({"role": "user", "content": user_message})

    print(f"{Fore.CYAN}Thinking...{Style.RESET_ALL}")
    resp = get_llm_response(history)
    history.append({"role": "assistant", "content": resp})
    processed = process_thinking_blocks(resp)
    print(f"{Fore.GREEN}{processed}{Style.RESET_ALL}")

    # Apply proposed edits with GUI diff per file
    for fp, _, _ in file_items:
        original = read_file_content(fp) or ""
        modified = extract_modified_content(resp, fp)
        if not modified or modified.strip() == original.strip():
            print(f"{Fore.YELLOW}No detectable changes for {fp}{Style.RESET_ALL}")
            continue

        merged, applied = gui_apply_changes_by_hunk(original, modified, fp)
        if applied:
            if write_file_content(fp, merged):
                print(f"{Fore.GREEN}Applied changes to {fp}{Style.RESET_ALL}")
        else:
            print(f"{Fore.YELLOW}Skipped changes for {fp}{Style.RESET_ALL}")


def handle_run_query(user_input, history):
    run_query = extract_run_query(user_input)
    clean_query, file_items, _ = extract_file_paths_and_urls(run_query)

    user_message = f"Command Request: {clean_query}\n"
    user_message += "Please suggest a single command to run. Put the command in a triple-backtick code block.\n"

    if file_items:
        user_message += "\nFiles:\n"
        for fp, s, e in file_items:
            c = read_file_content(fp, s, e)
            if c:
                user_message += f"File: {fp}\nContent:\n{c}\n\n"

    history.append({"role": "user", "content": user_message})

    print(f"\n{Fore.YELLOW}Thinking about what command to run...{Style.RESET_ALL}\n")
    resp = get_llm_response(history)
    history.append({"role": "assistant", "content": resp})

    processed = process_thinking_blocks(resp)
    print(f"{Fore.CYAN}🤖 Assistant:{Style.RESET_ALL}\n{processed}")

    # Extract first line from first code block, fallback: first non-empty line
    cmd = None
    m = re.search(r"```(?:bash|shell|cmd|powershell|sh)?\s*([\s\S]*?)```", processed)
    if m:
        cmd = m.group(1).strip().splitlines()[0].strip()
    else:
        for line in processed.splitlines():
            line = line.strip()
            if line:
                cmd = line
                break

    if not cmd:
        print(f"{Fore.YELLOW}Could not extract a command.{Style.RESET_ALL}")
        return

    ok, reason = is_safe_command(cmd)
    if not ok:
        print(f"{Fore.RED}Warning: Suggested command may be unsafe: {cmd}{Style.RESET_ALL}")
        print(f"{Fore.RED}Reason: {reason}{Style.RESET_ALL}")
        confirm = input(f"{Fore.YELLOW}Run anyway? (y/n): {Style.RESET_ALL}").lower()
        if confirm not in ('y', 'yes'):
            return

    print(f"\n{Fore.CYAN}Suggested command:{Style.RESET_ALL}\n{Fore.GREEN}{cmd}{Style.RESET_ALL}")
    confirm = input(f"{Fore.YELLOW}Run this command? (y/n): {Style.RESET_ALL}").lower()
    if confirm in ('y', 'yes'):
        output = execute_command(cmd)
        print(f"{Fore.CYAN}Command output:{Style.RESET_ALL}\n{output}")
        history.append({"role": "system", "content": f"Executed: {cmd}\nOutput:\n{output}"})


def handle_model_query(user_input, history):
    global CURRENT_MODEL
    model_name = extract_model_query(user_input)
    if not model_name:
        print(f"{Fore.YELLOW}No model name found.{Style.RESET_ALL}")
        return
    confirm = input(f"{Fore.YELLOW}Change model from {CURRENT_MODEL} to {model_name}? (y/n): {Style.RESET_ALL}").lower()
    if confirm in ('y', 'yes'):
        CURRENT_MODEL = model_name
        history.append({"role": "system", "content": f"Model changed to {CURRENT_MODEL}."})
        print(f"{Fore.CYAN}Model set to: {CURRENT_MODEL}{Style.RESET_ALL}")


def handle_create_query(user_input, history):
    create_query = extract_create_query(user_input)
    _, file_items, _ = extract_file_paths_and_urls(create_query)

    if not file_items:
        print(f"{Fore.YELLOW}No file specified. Use create: [path/to/file].{Style.RESET_ALL}")
        return

    for fp, _, _ in file_items:
        abs_fp = os.path.join(WORKING_DIRECTORY, fp) if WORKING_DIRECTORY and not os.path.isabs(fp) else fp
        if os.path.exists(abs_fp):
            confirm = input(f"{Fore.YELLOW}File exists: {fp}. Overwrite empty? (y/n): {Style.RESET_ALL}").lower()
            if confirm != 'y':
                continue
        confirm = input(f"{Fore.YELLOW}Create '{fp}'? (y/n): {Style.RESET_ALL}").lower()
        if confirm == 'y':
            Path(abs_fp).parent.mkdir(parents=True, exist_ok=True)
            Path(abs_fp).write_text("")
            history.append({"role": "system", "content": f"Created file '{fp}'."})
            print(f"{Fore.GREEN}Created: {fp}{Style.RESET_ALL}")


def handle_plan_query(user_input, history, model=None, timeout=None):
    """
    Plan mode:
    1) analysis prompt
    2) ask for JSON array steps:
        - create_file
        - write_code
        - edit_file
        - run_command
        - run_command_and_check

    Execution:
    - create_file: create empty
    - write_code: GUI diff (old vs new), hunk accept/reject, then write merged
    - edit_file: apply replace to compute proposed, GUI diff, then write merged
    - run_command: confirm + run
    - run_command_and_check: confirm + run + verify output
    """
    plan_description = extract_plan_query(user_input)

    # Collect bracket files as context (optional)
    bracket_files = re.findall(r'\[([^\]]+)\]', user_input)
    file_context = ""
    for fp in bracket_files:
        if fp.startswith("http"):
            continue
        c = read_file_content(fp)
        if c:
            file_context += f"--- {fp} ---\n{c}\n\n"

    analysis_prompt = f"""You are an AI assistant helping implement a project. The user's request is:
{plan_description}

{file_context}
Analyze requirements and approach. Do NOT output a plan yet. Keep it brief."""
    history.append({"role": "user", "content": analysis_prompt})

    print(f"{Fore.CYAN}Analyzing request...{Style.RESET_ALL}")
    analysis_resp = get_llm_response(history, model=model, timeout=timeout)
    history.append({"role": "assistant", "content": analysis_resp})
    print(f"{Fore.GREEN}{process_thinking_blocks(analysis_resp)}{Style.RESET_ALL}")

    planning_prompt = """Now provide a step-by-step plan as ONLY a JSON ARRAY.
Each step must be one of:
1) {"type":"create_file","file_path":"example.py"}
2) {"type":"write_code","file_path":"example.py","code":"..."}  (overwrites if applied)
3) {"type":"edit_file","file_path":"example.py","original_pattern":"...","new_content":"..."}  (string replace)
4) {"type":"run_command","command":"..."}
5) {"type":"run_command_and_check","command":"...","expected_output":"..."}

Return ONLY valid JSON array. No markdown. No explanations."""
    history.append({"role": "user", "content": planning_prompt})

    print(f"{Fore.CYAN}Generating plan...{Style.RESET_ALL}")
    plan_resp = get_llm_response(history, model=model, timeout=timeout)
    history.append({"role": "assistant", "content": plan_resp})

    # extract JSON array
    steps = None
    try:
        cleaned = process_thinking_blocks(plan_resp)
        start = cleaned.find("[")
        end = cleaned.rfind("]") + 1
        if start >= 0 and end > start:
            steps = json.loads(cleaned[start:end])
    except Exception:
        steps = None

    if not steps or not isinstance(steps, list):
        print(f"{Fore.RED}Failed to parse plan JSON. Raw response:{Style.RESET_ALL}\n{plan_resp}")
        return

    print(f"{Fore.GREEN}Generated Plan:{Style.RESET_ALL}")
    for i, st in enumerate(steps, 1):
        print(f"{Fore.CYAN}{i}. {st.get('type')} {st}{Style.RESET_ALL}")

    proceed = input(f"{Fore.YELLOW}Execute the plan? (y/n): {Style.RESET_ALL}").lower()
    if proceed not in ("y", "yes"):
        return

    for i, st in enumerate(steps, 1):
        st_type = st.get("type")
        print(f"{Fore.CYAN}\n=== Step {i}/{len(steps)}: {st_type} ==={Style.RESET_ALL}")

        if st_type == "create_file":
            fp = st.get("file_path")
            if not fp:
                print(f"{Fore.RED}Missing file_path.{Style.RESET_ALL}")
                continue
            abs_fp = os.path.join(WORKING_DIRECTORY, fp) if WORKING_DIRECTORY and not os.path.isabs(fp) else fp
            if os.path.exists(abs_fp):
                ow = input(f"{Fore.YELLOW}File exists. Overwrite empty? (y/n): {Style.RESET_ALL}").lower()
                if ow not in ("y", "yes"):
                    continue
            Path(abs_fp).parent.mkdir(parents=True, exist_ok=True)
            Path(abs_fp).write_text("")
            print(f"{Fore.GREEN}Created: {fp}{Style.RESET_ALL}")
            history.append({"role": "system", "content": f"Created file {fp}"})

        elif st_type == "write_code":
            fp = st.get("file_path")
            code = st.get("code", "")
            if not fp:
                print(f"{Fore.RED}Missing file_path.{Style.RESET_ALL}")
                continue

            original = read_file_content(fp) or ""
            merged, applied = gui_apply_changes_by_hunk(original, code, fp)

            if not applied:
                print(f"{Fore.YELLOW}Skipped writing: {fp}{Style.RESET_ALL}")
                continue

            ok = write_file_content(fp, merged)
            if ok:
                print(f"{Fore.GREEN}Wrote (merged) to: {fp}{Style.RESET_ALL}")
                history.append({"role": "system", "content": f"Wrote code to {fp} (with GUI diff merge)."})
            else:
                print(f"{Fore.RED}Write failed: {fp}{Style.RESET_ALL}")

        elif st_type == "edit_file":
            fp = st.get("file_path")
            pat = st.get("original_pattern", "")
            rep = st.get("new_content", "")
            if not fp:
                print(f"{Fore.RED}Missing file_path.{Style.RESET_ALL}")
                continue

            current = read_file_content(fp)
            if current is None:
                print(f"{Fore.RED}Cannot read file: {fp}{Style.RESET_ALL}")
                continue

            if pat not in current:
                print(f"{Fore.YELLOW}Pattern not found in {fp}. Skipping edit.{Style.RESET_ALL}")
                continue

            proposed = current.replace(pat, rep)
            merged, applied = gui_apply_changes_by_hunk(current, proposed, fp)

            if not applied:
                print(f"{Fore.YELLOW}Skipped edit: {fp}{Style.RESET_ALL}")
                continue

            ok = write_file_content(fp, merged)
            if ok:
                print(f"{Fore.GREEN}Edited (merged) file: {fp}{Style.RESET_ALL}")
                history.append({"role": "system", "content": f"Edited {fp} (GUI diff merge)."})
            else:
                print(f"{Fore.RED}Edit write failed: {fp}{Style.RESET_ALL}")

        elif st_type == "run_command":
            cmd = st.get("command")
            if not cmd:
                print(f"{Fore.RED}Missing command.{Style.RESET_ALL}")
                continue
            ok, reason = is_safe_command(cmd)
            if not ok:
                print(f"{Fore.RED}Unsafe command: {cmd}{Style.RESET_ALL}\nReason: {reason}")
                c = input(f"{Fore.YELLOW}Run anyway? (y/n): {Style.RESET_ALL}").lower()
                if c not in ("y", "yes"):
                    continue
            c = input(f"{Fore.YELLOW}Run command now? (y/n): {Style.RESET_ALL}").lower()
            if c in ("y", "yes"):
                out = execute_command(cmd)
                print(f"{Fore.CYAN}Output:{Style.RESET_ALL}\n{out}")
                history.append({"role": "system", "content": f"Ran: {cmd}\n{out}"})

        elif st_type == "run_command_and_check":
            cmd = st.get("command")
            exp = st.get("expected_output", "")
            if not cmd:
                print(f"{Fore.RED}Missing command.{Style.RESET_ALL}")
                continue
            ok, reason = is_safe_command(cmd)
            if not ok:
                print(f"{Fore.RED}Unsafe command: {cmd}{Style.RESET_ALL}\nReason: {reason}")
                c = input(f"{Fore.YELLOW}Run anyway? (y/n): {Style.RESET_ALL}").lower()
                if c not in ("y", "yes"):
                    continue
            c = input(f"{Fore.YELLOW}Run command now? (y/n): {Style.RESET_ALL}").lower()
            if c in ("y", "yes"):
                out = execute_command(cmd)
                print(f"{Fore.CYAN}Output:{Style.RESET_ALL}\n{out}")
                # very simple check: stdout contains expected_output
                if exp.strip() and exp.strip() in out:
                    print(f"{Fore.GREEN}Check passed (expected output found).{Style.RESET_ALL}")
                else:
                    print(f"{Fore.RED}Check failed (expected output not found).{Style.RESET_ALL}")
                history.append({"role": "system", "content": f"Ran+checked: {cmd}\nExpected:\n{exp}\nOutput:\n{out}"})

        else:
            print(f"{Fore.YELLOW}Unknown step type: {st_type}{Style.RESET_ALL}")

        cont = input(f"{Fore.YELLOW}Continue to next step? (y/n): {Style.RESET_ALL}").lower()
        if cont not in ("y", "yes"):
            break

    print(f"{Fore.GREEN}\nPlan execution completed.{Style.RESET_ALL}")


# =========================
# Main
# =========================

def main():
    global WORKING_DIRECTORY, SHOW_THINKING, CURRENT_MODEL, DEFAULT_TIMEOUT

    print(f"{Fore.CYAN}🤖 Local Coding Assistant (Custom /v1/chat) 🤖{Style.RESET_ALL}")
    print(f"{Fore.CYAN}================================================{Style.RESET_ALL}")
    print(f"{Fore.CYAN}Chat API URL: {CHAT_API_URL}{Style.RESET_ALL}")
    print(f"{Fore.CYAN}Model: {CURRENT_MODEL}{Style.RESET_ALL}")
    print()

    default_dir = os.getcwd()
    wd = input(f"{Fore.GREEN}Enter working directory path (default: current): {Style.RESET_ALL}").strip()
    if not wd:
        wd = default_dir
    if not os.path.exists(wd):
        try:
            os.makedirs(wd)
            print(f"{Fore.GREEN}Created working directory: {wd}{Style.RESET_ALL}")
        except Exception as e:
            print(f"{Fore.RED}Failed to create directory: {e}{Style.RESET_ALL}")
            wd = default_dir

    WORKING_DIRECTORY = os.path.abspath(wd)
    os.chdir(WORKING_DIRECTORY)

    print(f"{Fore.GREEN}Working directory set to: {WORKING_DIRECTORY}{Style.RESET_ALL}")
    print()
    print("Commands:")
    print("  search: ...   | edit: ... | run: ... | plan: ... | model: ... | create: [file]")
    print("  thinking:on / thinking:off | thinking:length N | timeout: N")
    print("  files | reset | clear | exit")
    print()

    history = []

    while True:
        try:
            user_input = input(f"{Fore.YELLOW}> {Style.RESET_ALL}").strip()
            if not user_input:
                continue

            if user_input.lower() in ("exit", "quit", "bye", ":q"):
                break

            if user_input.lower() in ("clear",):
                os.system("cls" if os.name == "nt" else "clear")
                continue

            if user_input.lower() in ("reset",):
                history.clear()
                print(f"{Fore.CYAN}Conversation history reset.{Style.RESET_ALL}")
                continue

            if user_input.lower() in ("files",):
                files = get_file_list()
                print(f"{Fore.CYAN}Files:{Style.RESET_ALL}")
                for f in files[:200]:
                    print(f"  - {f}")
                if len(files) > 200:
                    print(f"  ... and {len(files)-200} more")
                continue

            if user_input.lower() in ("thinking:on", "thinking on"):
                SHOW_THINKING = True
                print(f"{Fore.CYAN}Thinking display is now ON{Style.RESET_ALL}")
                continue

            if user_input.lower() in ("thinking:off", "thinking off"):
                SHOW_THINKING = False
                print(f"{Fore.CYAN}Thinking display is now OFF{Style.RESET_ALL}")
                continue

            if user_input.lower().startswith(("thinking:length ", "thinking length ")):
                try:
                    n = int(user_input.split()[-1])
                    n = max(100, n)
                    globals()["MAX_THINKING_LENGTH"] = n
                    print(f"{Fore.CYAN}MAX_THINKING_LENGTH set to {n}{Style.RESET_ALL}")
                except Exception:
                    print(f"{Fore.YELLOW}Usage: thinking:length 2000{Style.RESET_ALL}")
                continue

            if user_input.lower().startswith(("timeout:", "timeout ")):
                try:
                    n = int(user_input.split()[-1])
                    DEFAULT_TIMEOUT = max(1, n)
                    print(f"{Fore.CYAN}Timeout set to {DEFAULT_TIMEOUT}s{Style.RESET_ALL}")
                except Exception:
                    print(f"{Fore.YELLOW}Usage: timeout: 500{Style.RESET_ALL}")
                continue

            if is_search_query(user_input):
                handle_search_query(user_input, history)
            elif is_edit_query(user_input):
                handle_edit_query(user_input, history)
            elif is_run_query(user_input):
                handle_run_query(user_input, history)
            elif is_model_query(user_input):
                handle_model_query(user_input, history)
            elif is_create_query(user_input):
                handle_create_query(user_input, history)
            elif is_plan_query(user_input):
                handle_plan_query(user_input, history, model=CURRENT_MODEL, timeout=DEFAULT_TIMEOUT)
            else:
                handle_regular_query(user_input, history)

        except KeyboardInterrupt:
            print(f"\n{Fore.YELLOW}Interrupted. Type 'exit' to quit.{Style.RESET_ALL}")
            continue
        except EOFError:
            print("\nEOF. Exiting...")
            break
        except Exception as e:
            print(f"{Fore.RED}Unexpected error: {e}{Style.RESET_ALL}")

    print(f"{Fore.CYAN}Bye!{Style.RESET_ALL}")


if __name__ == "__main__":
    main()
