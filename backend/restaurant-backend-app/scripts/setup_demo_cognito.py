"""Safely provision fictional Cognito demo accounts for the restaurant application.

Default mode is a local plan and makes no AWS calls. --apply is required to
create missing groups/users. Passwords are prompted securely and never stored.
"""

from __future__ import annotations

import argparse
import getpass
import json
import os
import re
import sys
import tempfile
from pathlib import Path
from typing import Any

import boto3
from botocore.exceptions import ClientError

EXPECTED_ACCOUNT = "135483674259"
EXPECTED_REGION = "us-east-2"
EXPECTED_POOL_ID = "us-east-2_zcDc6uWRX"
OUTPUT_PATH = Path(__file__).with_name("demo_cognito_ids.json")

ACCOUNTS = [
    {"key": "owner", "email": "demo.owner@example.com", "first": "Alex", "last": "Morgan", "group": "Customer"},
    {"key": "waiter1", "email": "demo.waiter.juniper1@example.com", "first": "Mila", "last": "Petrenko", "group": "Waiter"},
    {"key": "waiter2", "email": "demo.waiter.juniper2@example.com", "first": "Oleh", "last": "Kovalenko", "group": "Waiter"},
    {"key": "waiter3", "email": "demo.waiter.harbor1@example.com", "first": "Iryna", "last": "Melnyk", "group": "Waiter"},
    {"key": "waiter4", "email": "demo.waiter.harbor2@example.com", "first": "Danylo", "last": "Shevchenko", "group": "Waiter"},
    {"key": "waiter5", "email": "demo.waiter.cedar1@example.com", "first": "Sofia", "last": "Bondar", "group": "Waiter"},
    {"key": "waiter6", "email": "demo.waiter.cedar2@example.com", "first": "Marko", "last": "Tkachenko", "group": "Waiter"},
]


def list_all_groups(client: Any, pool_id: str) -> dict[str, dict[str, Any]]:
    groups: dict[str, dict[str, Any]] = {}
    token = None
    while True:
        params: dict[str, Any] = {"UserPoolId": pool_id, "Limit": 60}
        if token:
            params["NextToken"] = token
        response = client.list_groups(**params)
        for group in response.get("Groups", []):
            groups[group["GroupName"]] = group
        token = response.get("NextToken")
        if not token:
            return groups


def get_existing_user(client: Any, pool_id: str, username: str) -> dict[str, Any] | None:
    try:
        return client.admin_get_user(UserPoolId=pool_id, Username=username)
    except client.exceptions.UserNotFoundException:
        return None


def user_sub(user: dict[str, Any]) -> str:
    for attribute in user.get("UserAttributes", []):
        if attribute.get("Name") == "sub":
            return attribute["Value"]
    raise RuntimeError(f"Cognito did not return a sub for user {user.get('Username', '<unknown>')}.")


def check_password(password: str, policy: dict[str, Any]) -> list[str]:
    issues = []
    minimum = policy.get("MinimumLength", 8)
    if minimum > 16:
        issues.append(f"the pool requires at least {minimum} characters, but the application registration limit is 16")
    elif len(password) < minimum:
        issues.append(f"at least {minimum} characters")
    if len(password) > 16:
        issues.append("no more than 16 characters, matching the application's registration limit")
    checks = [
        ("RequireUppercase", r"[A-Z]", "an uppercase letter"),
        ("RequireLowercase", r"[a-z]", "a lowercase letter"),
        ("RequireNumbers", r"[0-9]", "a number"),
        ("RequireSymbols", r"[^A-Za-z0-9]", "a symbol"),
    ]
    for policy_key, pattern, label in checks:
        if policy.get(policy_key, False) and re.search(pattern, password) is None:
            issues.append(label)
    return issues


def prompt_password(email: str, policy: dict[str, Any]) -> str:
    while True:
        password = getpass.getpass(f"Set password for {email}: ")
        confirmation = getpass.getpass("Confirm password: ")
        if password != confirmation:
            print("Passwords did not match; try again.", file=sys.stderr)
            continue
        issues = check_password(password, policy)
        if issues:
            print("Password must contain " + ", ".join(issues) + ".", file=sys.stderr)
            continue
        return password


def write_ids_file(path: Path, owner_id: str, waiter_ids: list[str]) -> None:
    payload = {"ownerUserId": owner_id, "waiterUserIds": waiter_ids}
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix="demo-cognito-", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
            json.dump(payload, output, indent=2)
            output.write("\n")
        os.replace(temporary_name, path)
    except Exception:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass
        raise


def run(args: argparse.Namespace) -> None:
    print(f"Target: AWS account {args.expected_account}, region {args.region}, user pool {args.user_pool_id}")
    print("Planned fictional accounts:")
    for account in ACCOUNTS:
        print(f"  {account['group']}: {account['first']} {account['last']} <{account['email']}>")
    print("Required Cognito groups: Customer (Owner) and Waiter (six staff accounts).")
    print(f"IDs output: {args.output}")
    if not args.apply:
        print("Preview only. No AWS credentials were used and no Cognito changes were made.")
        return

    session = boto3.Session(profile_name=args.profile, region_name=args.region)
    account_id = session.client("sts").get_caller_identity()["Account"]
    if account_id != args.expected_account:
        raise RuntimeError(f"Refusing to write: AWS account is {account_id}, expected {args.expected_account}.")

    cognito = session.client("cognito-idp")
    pool = cognito.describe_user_pool(UserPoolId=args.user_pool_id)["UserPool"]
    if pool.get("Id") != EXPECTED_POOL_ID or args.user_pool_id != EXPECTED_POOL_ID:
        raise RuntimeError(f"Refusing to write: unexpected user pool {pool.get('Id')}.")

    provided_attributes = {"email", "email_verified", "given_name", "family_name"}
    required_attributes = {
        attribute.get("Name")
        for attribute in pool.get("SchemaAttributes", [])
        if attribute.get("Required")
    }
    missing_attributes = required_attributes - provided_attributes - {"sub"}
    if missing_attributes:
        raise RuntimeError(
            "The pool requires attributes this demo setup will not invent: "
            + ", ".join(sorted(missing_attributes))
            + ". No Cognito changes were made."
        )

    username_attributes = set(pool.get("UsernameAttributes") or [])
    aliases = set(pool.get("AliasAttributes") or [])
    if "email" not in username_attributes and "email" not in aliases:
        raise RuntimeError("The pool does not support email sign-in, which the application SignInHandler requires.")

    clients = []
    token = None
    while True:
        params = {"UserPoolId": args.user_pool_id, "MaxResults": 60}
        if token:
            params["NextToken"] = token
        response = cognito.list_user_pool_clients(**params)
        clients.extend(response.get("UserPoolClients", []))
        token = response.get("NextToken")
        if not token:
            break
    compatible_clients = []
    for item in clients:
        detail = cognito.describe_user_pool_client(
            UserPoolId=args.user_pool_id,
            ClientId=item["ClientId"],
        )["UserPoolClient"]
        flows = set(detail.get("ExplicitAuthFlows") or [])
        if "ALLOW_USER_PASSWORD_AUTH" in flows and not detail.get("ClientSecret"):
            compatible_clients.append(detail)
    client_ids = {item["ClientId"] for item in compatible_clients}
    if args.client_id:
        if args.client_id not in client_ids:
            raise RuntimeError("The selected app client is not configured for USER_PASSWORD_AUTH without a client secret.")
        selected_client_id = args.client_id
    elif len(client_ids) == 1:
        selected_client_id = next(iter(client_ids))
    else:
        raise RuntimeError(
            "Expected exactly one app client compatible with the application's USER_PASSWORD_AUTH flow; "
            f"found {len(client_ids)}. Supply --client-id from the API Lambda's COGNITO_USER_POOL_CLIENT_ID configuration."
        )
    print(f"Compatible email/password app client: {selected_client_id}")

    groups = list_all_groups(cognito, args.user_pool_id)
    existing_users: dict[str, dict[str, Any] | None] = {}
    for item in ACCOUNTS:
        user = get_existing_user(cognito, args.user_pool_id, item["email"])
        existing_users[item["key"]] = user
        if user is not None:
            if user.get("UserStatus") != "CONFIRMED" or not user.get("Enabled", True):
                raise RuntimeError(
                    f"Existing account {item['email']} is not enabled/confirmed; refusing to modify it."
                )
            memberships = cognito.admin_list_groups_for_user(
                UserPoolId=args.user_pool_id,
                Username=item["email"],
                Limit=60,
            ).get("Groups", [])
            if item["group"] not in {membership["GroupName"] for membership in memberships}:
                raise RuntimeError(
                    f"Existing account {item['email']} is not in required group {item['group']}; "
                    "refusing to modify existing users. Resolve this manually and rerun."
                )

    password_policy = pool.get("Policies", {}).get("PasswordPolicy", {})
    passwords = {
        item["key"]: prompt_password(item["email"], password_policy)
        for item in ACCOUNTS
        if existing_users[item["key"]] is None
    }

    for role in ("Customer", "Waiter"):
        if role not in groups:
            cognito.create_group(UserPoolId=args.user_pool_id, GroupName=role, Description=f"{role} application group")
            print(f"Created Cognito group {role}.")

    resolved: dict[str, str] = {}
    for item in ACCOUNTS:
        user = existing_users[item["key"]]
        if user is None:
            cognito.admin_create_user(
                UserPoolId=args.user_pool_id,
                Username=item["email"],
                MessageAction="SUPPRESS",
                UserAttributes=[
                    {"Name": "email", "Value": item["email"]},
                    {"Name": "email_verified", "Value": "true"},
                    {"Name": "given_name", "Value": item["first"]},
                    {"Name": "family_name", "Value": item["last"]},
                ],
            )
            cognito.admin_set_user_password(
                UserPoolId=args.user_pool_id,
                Username=item["email"],
                Password=passwords[item["key"]],
                Permanent=True,
            )
            cognito.admin_add_user_to_group(
                UserPoolId=args.user_pool_id,
                Username=item["email"],
                GroupName=item["group"],
            )
            user = cognito.admin_get_user(UserPoolId=args.user_pool_id, Username=item["email"])
            print(f"Created {item['group']} account {item['email']}.")
        else:
            print(f"Reused existing correctly configured account {item['email']} without modifying it.")
        resolved[item["key"]] = user_sub(user)

    write_ids_file(args.output, resolved["owner"], [resolved[f"waiter{index}"] for index in range(1, 7)])
    print(f"Saved Cognito sub IDs to {args.output}. No passwords were written or printed.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", default="restaurant2")
    parser.add_argument("--region", default=EXPECTED_REGION)
    parser.add_argument("--expected-account", default=EXPECTED_ACCOUNT)
    parser.add_argument("--user-pool-id", default=EXPECTED_POOL_ID)
    parser.add_argument("--client-id", help="Optional compatible app client ID when more than one exists.")
    parser.add_argument("--output", type=Path, default=OUTPUT_PATH)
    parser.add_argument("--apply", action="store_true", help="Create only missing groups/users in the verified pool.")
    args = parser.parse_args()
    if args.region != EXPECTED_REGION or args.expected_account != EXPECTED_ACCOUNT or args.user_pool_id != EXPECTED_POOL_ID:
        parser.error("This demo setup is pinned to the specified account, region, and user pool.")
    run(args)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ClientError as error:
        code = error.response.get("Error", {}).get("Code", "AWS error")
        print(f"Cognito setup failed ({code}); inspect AWS state before retrying.", file=sys.stderr)
        sys.exit(1)
