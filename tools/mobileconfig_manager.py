#!/usr/bin/env python3
"""
Apple Mail .mobileconfig Profile Manager

Creates and edits .mobileconfig files for pre-configuring IMAP accounts
in Apple Mail. Supports multiple accounts per profile.

No external dependencies — uses only Python stdlib.
"""

import plistlib
import uuid
import os
import sys


# ── Defaults (pre-filled for mail archiver) ──────────────────────────────────

DEFAULTS = {
    "imap_host": "172.16.10.106",
    "imap_port": 143,
    "imap_ssl": False,
    "smtp_host": "mail.re-macs.com",
    "smtp_port": 465,
    "smtp_ssl": True,
}


# ── Helpers ──────────────────────────────────────────────────────────────────

def prompt(label, default=None):
    """Prompt for input with an optional default."""
    if default is not None:
        raw = input(f"  {label} [{default}]: ").strip()
        return raw if raw else str(default)
    return input(f"  {label}: ").strip()


def prompt_bool(label, default=False):
    """Prompt for yes/no."""
    hint = "Y/n" if default else "y/N"
    raw = input(f"  {label} [{hint}]: ").strip().lower()
    if not raw:
        return default
    return raw in ("y", "yes")


def make_account_payload(email, imap_password, imap_host, imap_port,
                         imap_ssl, smtp_host, smtp_port, smtp_ssl):
    """Build a single email account payload dict."""
    account_name = email.split("@")[0].title()
    return {
        "EmailAccountDescription": f"Mail Archive - {email}",
        "EmailAccountName": account_name,
        "EmailAccountType": "EmailTypeIMAP",
        "EmailAddress": email,
        "IncomingMailServerAuthentication": "EmailAuthPassword",
        "IncomingMailServerHostName": imap_host,
        "IncomingMailServerPortNumber": int(imap_port),
        "IncomingMailServerUseSSL": imap_ssl,
        "IncomingMailServerUsername": email,
        "IncomingPassword": imap_password,
        "OutgoingMailServerAuthentication": "EmailAuthPassword" if smtp_host else "EmailAuthNone",
        "OutgoingMailServerHostName": smtp_host or "localhost",
        "OutgoingMailServerPortNumber": int(smtp_port),
        "OutgoingMailServerUseSSL": smtp_ssl,
        "OutgoingMailServerUsername": email,
        "OutgoingPassword": "",
        "PayloadIdentifier": f"com.mailarchiver.email.{uuid.uuid4()}",
        "PayloadType": "com.apple.mail.managed",
        "PayloadUUID": str(uuid.uuid4()),
        "PayloadVersion": 1,
        "PayloadDisplayName": f"Mail Archive - {email}",
    }


def make_profile(display_name, payloads):
    """Wrap account payloads in a profile envelope."""
    return {
        "PayloadContent": payloads,
        "PayloadDescription": display_name,
        "PayloadDisplayName": display_name,
        "PayloadIdentifier": f"com.mailarchiver.profile.{uuid.uuid4()}",
        "PayloadType": "Configuration",
        "PayloadUUID": str(uuid.uuid4()),
        "PayloadVersion": 1,
    }


def extract_account_info(payload):
    """Extract display fields from an account payload."""
    return {
        "email": payload.get("EmailAddress", "?"),
        "imap_host": payload.get("IncomingMailServerHostName", "?"),
        "imap_port": payload.get("IncomingMailServerPortNumber", "?"),
        "imap_ssl": payload.get("IncomingMailServerUseSSL", False),
        "smtp_host": payload.get("OutgoingMailServerHostName", "?"),
        "smtp_port": payload.get("OutgoingMailServerPortNumber", "?"),
        "smtp_ssl": payload.get("OutgoingMailServerUseSSL", False),
    }


# ── Account prompts ─────────────────────────────────────────────────────────

def prompt_account():
    """Interactively collect account details."""
    print()
    email = prompt("Email address")
    if not email:
        print("  Email is required.")
        return None
    imap_password = prompt("IMAP password")
    imap_host = prompt("IMAP host", DEFAULTS["imap_host"])
    imap_port = prompt("IMAP port", DEFAULTS["imap_port"])
    imap_ssl = prompt_bool("IMAP use SSL?", DEFAULTS["imap_ssl"])
    smtp_host = prompt("SMTP host", DEFAULTS["smtp_host"])
    smtp_port = prompt("SMTP port", DEFAULTS["smtp_port"])
    smtp_ssl = prompt_bool("SMTP use SSL?", DEFAULTS["smtp_ssl"])

    return make_account_payload(
        email, imap_password, imap_host, imap_port,
        imap_ssl, smtp_host, smtp_port, smtp_ssl,
    )


def prompt_edit_account(payload):
    """Edit an existing account payload in place."""
    print(f"\n  Editing: {payload.get('EmailAddress', '?')}")
    print("  (Press Enter to keep current value)\n")

    email = prompt("Email address", payload.get("EmailAddress", ""))
    imap_password = prompt("IMAP password (leave empty to keep)", "")
    imap_host = prompt("IMAP host", payload.get("IncomingMailServerHostName", ""))
    imap_port = prompt("IMAP port", payload.get("IncomingMailServerPortNumber", ""))
    imap_ssl = prompt_bool("IMAP use SSL?", payload.get("IncomingMailServerUseSSL", False))
    smtp_host = prompt("SMTP host", payload.get("OutgoingMailServerHostName", ""))
    smtp_port = prompt("SMTP port", payload.get("OutgoingMailServerPortNumber", ""))
    smtp_ssl = prompt_bool("SMTP use SSL?", payload.get("OutgoingMailServerUseSSL", False))

    payload["EmailAddress"] = email
    payload["IncomingMailServerUsername"] = email
    payload["OutgoingMailServerUsername"] = email
    payload["EmailAccountDescription"] = f"Mail Archive - {email}"
    payload["PayloadDisplayName"] = f"Mail Archive - {email}"
    payload["EmailAccountName"] = email.split("@")[0].title()
    if imap_password:
        payload["IncomingPassword"] = imap_password
    payload["IncomingMailServerHostName"] = imap_host
    payload["IncomingMailServerPortNumber"] = int(imap_port)
    payload["IncomingMailServerUseSSL"] = imap_ssl
    payload["OutgoingMailServerHostName"] = smtp_host
    payload["OutgoingMailServerPortNumber"] = int(smtp_port)
    payload["OutgoingMailServerUseSSL"] = smtp_ssl


# ── File I/O ─────────────────────────────────────────────────────────────────

def load_profile(path):
    """Load a .mobileconfig file."""
    with open(path, "rb") as f:
        return plistlib.load(f)


def save_profile(profile, path):
    """Save a profile to a .mobileconfig file."""
    with open(path, "wb") as f:
        plistlib.dump(profile, f, fmt=plistlib.FMT_XML)
    print(f"\n  Saved to: {path}")


def pick_file():
    """Open a file dialog or prompt for a path."""
    try:
        import tkinter as tk
        from tkinter import filedialog
        root = tk.Tk()
        root.withdraw()
        path = filedialog.askopenfilename(
            title="Select .mobileconfig file",
            filetypes=[("mobileconfig", "*.mobileconfig"), ("All files", "*.*")],
        )
        root.destroy()
        return path or None
    except Exception:
        path = input("  Enter path to .mobileconfig file: ").strip()
        return path if path and os.path.exists(path) else None


def pick_save_path(current_path=None):
    """Pick a save location."""
    if current_path:
        use_current = prompt_bool(f"Save to {current_path}?", True)
        if use_current:
            return current_path

    try:
        import tkinter as tk
        from tkinter import filedialog
        root = tk.Tk()
        root.withdraw()
        path = filedialog.asksaveasfilename(
            title="Save .mobileconfig file",
            defaultextension=".mobileconfig",
            filetypes=[("mobileconfig", "*.mobileconfig")],
        )
        root.destroy()
        return path or None
    except Exception:
        return input("  Enter save path: ").strip() or None


# ── Display ──────────────────────────────────────────────────────────────────

def list_accounts(profile):
    """Print all accounts in the profile."""
    payloads = profile.get("PayloadContent", [])
    if not payloads:
        print("\n  No accounts in this profile.")
        return

    print(f"\n  {len(payloads)} account(s):\n")
    for i, p in enumerate(payloads, 1):
        info = extract_account_info(p)
        ssl_tag = "SSL" if info["imap_ssl"] else "plain"
        print(f"  {i}. {info['email']}")
        print(f"     IMAP: {info['imap_host']}:{info['imap_port']} ({ssl_tag})")
        print(f"     SMTP: {info['smtp_host']}:{info['smtp_port']} ({'SSL' if info['smtp_ssl'] else 'plain'})")
        print()


def select_account(profile, action="select"):
    """Let the user pick an account by number."""
    payloads = profile.get("PayloadContent", [])
    if not payloads:
        print("\n  No accounts in this profile.")
        return None

    list_accounts(profile)
    try:
        idx = int(input(f"  Account number to {action} (0 to cancel): ")) - 1
        if idx < 0 or idx >= len(payloads):
            return None
        return idx
    except ValueError:
        return None


# ── Main menu ────────────────────────────────────────────────────────────────

def main():
    profile = None
    file_path = None

    print("\n  Apple Mail .mobileconfig Profile Manager")
    print("  =========================================\n")

    while True:
        has_profile = profile is not None

        print("\n  --- Menu ---")
        print("  1. Create new profile")
        print("  2. Open existing profile")
        if has_profile:
            print(f"  3. Add account          ({len(profile.get('PayloadContent', []))} accounts)")
            print("  4. List accounts")
            print("  5. Edit account")
            print("  6. Remove account")
            print("  7. Save")
        print("  0. Quit")

        choice = input("\n  Choice: ").strip()

        if choice == "0":
            if has_profile:
                if prompt_bool("Save before quitting?", True):
                    path = pick_save_path(file_path)
                    if path:
                        save_profile(profile, path)
            print("\n  Bye!\n")
            break

        elif choice == "1":
            name = prompt("Profile display name", "Mail Archive Accounts")
            profile = make_profile(name, [])
            file_path = None
            print(f"\n  Created new profile: {name}")

        elif choice == "2":
            path = pick_file()
            if path:
                try:
                    profile = load_profile(path)
                    file_path = path
                    count = len(profile.get("PayloadContent", []))
                    print(f"\n  Loaded: {path} ({count} account(s))")
                except Exception as e:
                    print(f"\n  Error loading file: {e}")

        elif choice == "3" and has_profile:
            payload = prompt_account()
            if payload:
                profile["PayloadContent"].append(payload)
                print(f"\n  Added: {payload['EmailAddress']}")

        elif choice == "4" and has_profile:
            list_accounts(profile)

        elif choice == "5" and has_profile:
            idx = select_account(profile, "edit")
            if idx is not None:
                prompt_edit_account(profile["PayloadContent"][idx])
                print("  Updated.")

        elif choice == "6" and has_profile:
            idx = select_account(profile, "remove")
            if idx is not None:
                removed = profile["PayloadContent"].pop(idx)
                print(f"  Removed: {removed.get('EmailAddress', '?')}")

        elif choice == "7" and has_profile:
            path = pick_save_path(file_path)
            if path:
                save_profile(profile, path)
                file_path = path

        else:
            print("  Invalid choice.")


if __name__ == "__main__":
    main()
