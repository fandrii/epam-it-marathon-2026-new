"""Append fictional demo records to the restaurant application's DynamoDB tables.

The default mode prints a data plan and makes no AWS calls. --apply is required to
write. Existing records are never overwritten; conflicting seed keys are skipped.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path
from typing import Any

import boto3
from botocore.exceptions import ClientError

EXPECTED_TABLES = {
    "Locations": "Locations",
    "Tables": "Tables",
    "Dishes": "Dishes",
    "Waiters": "Waiters",
    "Reservations": "Reservations",
    "GroupReservations": "GroupReservations",
    "GuestSessions": "GuestSessions",
    "Orders": "Orders",
    "Feedback": "Feedback",
    "Notifications": "Notifications",
    "Reports": "Reports",
    "ReportsHistory": "ReportsHistory",
    "ProfileImages": "ProfileImages",
}
PREFIX = "demo-v1-"
SEED_DATE = datetime(2026, 11, 1, 0, 0, tzinfo=timezone.utc)

LOCATIONS = [
    {
        "id": PREFIX + "location-juniper",
        "address": "104 Cedar Grove Avenue, Portland, OR 97205",
        "description": "Juniper & Stone - seasonal Pacific Northwest dining and an open hearth.",
        "totalCapacity": 24,
        "averageOccupancy": "38%",
        "imageUrl": None,
        "rating": Decimal("4.7"),
    },
    {
        "id": PREFIX + "location-harbor",
        "address": "218 Bayview Street, Seattle, WA 98101",
        "description": "Harbor & Hearth - coastal comfort food with views of Elliott Bay.",
        "totalCapacity": 24,
        "averageOccupancy": "52%",
        "imageUrl": None,
        "rating": Decimal("4.6"),
    },
    {
        "id": PREFIX + "location-cedar",
        "address": "63 Saffron Market Road, Austin, TX 78701",
        "description": "Cedar & Saffron - contemporary Texas cooking with bright global flavors.",
        "totalCapacity": 24,
        "averageOccupancy": "44%",
        "imageUrl": None,
        "rating": Decimal("4.8"),
    },
]

DISH_SPECS = [
    # id suffix, name, description, price, type, weight, calories, protein, fat, carbs, vitamins
    ("caesar", "Caesar Salad", "Crisp romaine, parmesan, house dressing, and sourdough croutons.", "180", 0, "280 g", "320 kcal", "12 g", "21 g", "19 g", "A, K"),
    ("tom-yum", "Tom Yum", "A fragrant lemongrass broth with mushrooms, lime, and herbs.", "220", 0, "380 g", "260 kcal", "18 g", "8 g", "27 g", "C, B6"),
    ("roasted-carrots", "Roasted Carrots", "Honey-roasted carrots with whipped feta and toasted seeds.", "160", 0, "240 g", "290 kcal", "8 g", "16 g", "31 g", "A, E"),
    ("mushroom-risotto", "Wild Mushroom Risotto", "Creamy arborio rice, forest mushrooms, and aged parmesan.", "320", 1, "390 g", "610 kcal", "19 g", "24 g", "76 g", "B2, B12"),
    ("salmon", "Cedar-Plank Salmon", "Herb-roasted salmon with seasonal vegetables and lemon butter.", "420", 1, "360 g", "540 kcal", "38 g", "27 g", "28 g", "D, B12"),
    ("chocolate-tart", "Chocolate Tart", "Dark chocolate ganache, flaky pastry, and fresh berries.", "190", 2, "150 g", "430 kcal", "6 g", "28 g", "40 g", "E, iron"),
]

LOCATION_DISH_PREFIXES = ["juniper", "harbor", "cedar"]


def iso(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def build_seed(owner_id: str, waiter_ids: list[str]) -> dict[str, list[dict[str, Any]]]:
    if len(waiter_ids) != 6 or len(set(waiter_ids)) != 6:
        raise ValueError("Exactly six distinct existing Cognito waiter sub values are required.")
    if not owner_id.strip() or owner_id in waiter_ids:
        raise ValueError("A non-empty owner Cognito sub distinct from waiter IDs is required.")

    records: dict[str, list[dict[str, Any]]] = {name: [] for name in EXPECTED_TABLES}
    records["Locations"] = [dict(location) for location in LOCATIONS]

    tables_by_location: dict[str, list[dict[str, Any]]] = {}
    for loc_index, location in enumerate(LOCATIONS):
        tables = []
        for number, capacity in enumerate((2, 2, 4, 4, 6, 6), start=1):
            table = {
                "id": f"{PREFIX}table-{LOCATION_DISH_PREFIXES[loc_index]}-{number:02d}",
                "locationId": location["id"],
                "tableNumber": number,
                "capacity": capacity,
            }
            tables.append(table)
            records["Tables"].append(table)
        tables_by_location[location["id"]] = tables

    dishes_by_location: dict[str, list[dict[str, Any]]] = {}
    for loc_index, location in enumerate(LOCATIONS):
        dishes = []
        for spec in DISH_SPECS:
            suffix, name, description, price, dish_type, weight, calories, proteins, fats, carbs, vitamins = spec
            dish = {
                "id": f"{PREFIX}dish-{LOCATION_DISH_PREFIXES[loc_index]}-{suffix}",
                "name": name,
                "description": description,
                "dishType": dish_type,
                "price": Decimal(price),
                "weight": weight,
                "calories": calories,
                "proteins": proteins,
                "fats": fats,
                "carbohydrates": carbs,
                "vitamins": vitamins,
                "imageUrl": None,
                "state": 1,
                # Dish.LocationId has no DynamoDBProperty attribute; the AWS .NET mapper
                # uses the CLR property name for this one attribute.
                "LocationId": location["id"],
            }
            dishes.append(dish)
            records["Dishes"].append(dish)
        dishes_by_location[location["id"]] = dishes
        records["Locations"][loc_index]["specialtyDishIds"] = [dishes[3]["id"], dishes[4]["id"]]

    now = datetime.now(timezone.utc).replace(minute=0, second=0, microsecond=0)
    reservations: list[dict[str, Any]] = []
    reservation_meta: list[dict[str, Any]] = []
    waiter_reservations: dict[str, list[str]] = {waiter_id: [] for waiter_id in waiter_ids}

    for loc_index, location in enumerate(LOCATIONS):
        location_waiters = waiter_ids[loc_index * 2 : loc_index * 2 + 2]
        dishes = dishes_by_location[location["id"]]
        for slot in range(3):
            reservation_id = f"{PREFIX}reservation-{LOCATION_DISH_PREFIXES[loc_index]}-{slot + 1:02d}"
            waiter_id = location_waiters[slot % 2]
            table = tables_by_location[location["id"]][slot]
            if slot < 2:
                start = now + timedelta(days=4 + loc_index + slot, hours=12 + slot)
                status = "Reserved"
                guest_count = 4 if slot == 0 else 1
            else:
                start = datetime(2026, 10, 28 + loc_index, 18, 0, tzinfo=timezone.utc)
                status = "Completed"
                guest_count = 2
            end = start + timedelta(hours=2)
            reservation = {
                "id": reservation_id,
                "tableId": table["id"],
                "customerId": owner_id,
                "waiterId": waiter_id,
                "createdByWaiter": False,
                "startTime": iso(start),
                "endTime": iso(end),
                "guestCount": guest_count,
                "status": status,
            }
            reservations.append(reservation)
            reservation_meta.append({
                "reservation": reservation,
                "location": location,
                "table": table,
                "waiterId": waiter_id,
                "dishes": dishes,
                "slot": slot,
            })
            waiter_reservations[waiter_id].append(reservation_id)
    records["Reservations"] = reservations

    for idx, waiter_id in enumerate(waiter_ids):
        records["Waiters"].append({
            "userId": waiter_id,
            "locationId": LOCATIONS[idx // 2]["id"],
            "createdAt": iso(SEED_DATE),
            "reservations": waiter_reservations[waiter_id],
        })

    for meta in reservation_meta:
        reservation = meta["reservation"]
        if reservation["status"] != "Reserved" or reservation["guestCount"] < 2:
            continue
        group_id = PREFIX + "group-" + reservation["id"][len(PREFIX + "reservation-"):]
        group_token = PREFIX + "invite-" + group_id[len(PREFIX + "group-"):]
        records["GroupReservations"].append({
            "groupReservationId": group_id,
            "reservationId": reservation["id"],
            "reservationToken": group_token,
            "status": "Open",
        })
        records["GuestSessions"].append({
            "guestSessionId": PREFIX + "session-owner-" + group_id[len(PREFIX + "group-"):],
            "groupReservationId": group_id,
            "guestName": "Owner",
            "dishes": [],
            "isOwnerSession": True,
            "ownerUserId": owner_id,
            "isSubmitted": False,
        })

    first_group = next(group for group in records["GroupReservations"] if "juniper-01" in group["groupReservationId"])
    first_dishes = dishes_by_location[LOCATIONS[0]["id"]]
    caesar = next(dish for dish in first_dishes if dish["name"] == "Caesar Salad")
    tom_yum = next(dish for dish in first_dishes if dish["name"] == "Tom Yum")
    records["GuestSessions"].extend([
        {
            "guestSessionId": PREFIX + "session-andrii",
            "groupReservationId": first_group["groupReservationId"],
            "guestName": "Andrii",
            "dishes": [{"dishId": caesar["id"], "quantity": 2, "state": 0}],
            "isOwnerSession": False,
            "isSubmitted": True,
        },
        {
            "guestSessionId": PREFIX + "session-olena",
            "groupReservationId": first_group["groupReservationId"],
            "guestName": "Olena",
            "dishes": [{"dishId": tom_yum["id"], "quantity": 1, "state": 0}],
            "isOwnerSession": False,
            "isSubmitted": False,
        },
    ])

    completed_meta = [meta for meta in reservation_meta if meta["reservation"]["status"] == "Completed"]
    for idx, meta in enumerate(reservation_meta):
        reservation = meta["reservation"]
        dishes = meta["dishes"]
        if reservation["status"] == "Completed":
            selected = [(dishes[3], 1), (dishes[5], 2)]
            item_state = 1
            order_status = 1
        elif reservation["guestCount"] == 1:
            selected = [(dishes[4], 1)]
            item_state = 0
            order_status = 0
        else:
            selected = []
            item_state = 0
            order_status = 0
        order_id = PREFIX + "order-" + reservation["id"][len(PREFIX + "reservation-"):]
        table_label = f"Table {meta['table']['tableNumber']}"
        records["Orders"].append({
            "orderId": order_id,
            "customerId": owner_id,
            "reservationId": reservation["id"],
            "reservationAddress": f"{meta['location']['address']}, {table_label}",
            "reservationTime": f"{reservation['startTime']} - {reservation['endTime']}",
            "dishes": [
                {"dishId": dish["id"], "quantity": quantity, "state": item_state}
                for dish, quantity in selected
            ],
            "status": order_status,
        })
        records["Notifications"].append({
            "id": PREFIX + "notification-" + reservation["id"][len(PREFIX + "reservation-"):],
            "userId": meta["waiterId"],
            "message": f"Demo order update for {reservation['id']}.",
            "orderId": order_id,
            "reservationId": reservation["id"],
            "isRead": False,
            "createdAt": iso(SEED_DATE + timedelta(hours=idx)),
        })

    feedback_comments = [
        "Warm service and a thoughtful menu.",
        "Everything arrived fresh and beautifully prepared.",
        "A relaxed evening with excellent seasonal dishes.",
    ]
    for idx, meta in enumerate(completed_meta):
        reservation = meta["reservation"]
        created = iso(SEED_DATE + timedelta(days=idx, hours=12))
        records["Feedback"].extend([
            {
                "reservationId": reservation["id"],
                "locationId": meta["location"]["id"],
                "userId": owner_id,
                "comment": feedback_comments[idx],
                "rating": 5 if idx % 2 == 0 else 4,
                "createdAt": created,
                "type": 0,
            },
            {
                "reservationId": reservation["id"],
                "locationId": meta["location"]["id"],
                "userId": owner_id,
                "comment": "The flavors were balanced and the portions generous.",
                "rating": 4 if idx % 2 == 0 else 5,
                "createdAt": iso(SEED_DATE + timedelta(days=idx, hours=13)),
                "type": 1,
            },
        ])

    today = datetime.now(timezone.utc).date()
    report_start = (today - timedelta(days=6)).isoformat()
    report_end = today.isoformat()
    for idx, location in enumerate(LOCATIONS):
        records["ReportsHistory"].append({
            "id": PREFIX + "location-report-" + LOCATION_DISH_PREFIXES[idx],
            "reportType": "Sales",
            "startDate": report_start,
            "endDate": report_end,
            "userId": owner_id,
            "locationId": location["id"],
            "locationName": location["description"].split(" - ", 1)[0],
            "totalOrders": 18 + idx * 4,
            "avgCuisineFeedback": Decimal("4.6") + Decimal("0.1") * idx,
            "minCuisineFeedback": 4,
            "revenue": Decimal("6840.00") + Decimal("1250.00") * idx,
            "deltaAvgCuisineFeedback": Decimal("0.2"),
            "deltaRevenue": Decimal("540.00") + Decimal("115.00") * idx,
        })

    for idx, waiter_id in enumerate(waiter_ids):
        location_index = idx // 2
        records["Reports"].append({
            "waiterId": waiter_id,
            "locationId": LOCATIONS[location_index]["id"],
            "averageServiceRating": Decimal("4.5") if idx % 2 == 0 else Decimal("4.8"),
            "minimumServiceRating": 4,
            "averageCuisineRating": Decimal("4.7"),
            "minimumCuisineRating": 4,
            "totalRevenue": Decimal("860.00") + Decimal(125 * idx),
            "hoursWorked": Decimal("6.5"),
            "date": iso(SEED_DATE),
            "ordersProcessed": len(waiter_reservations[waiter_id]),
        })

    assert len(records["Locations"]) == 3
    assert len(records["Tables"]) == 18
    assert len(records["Dishes"]) == 18
    assert len(records["Waiters"]) == 6
    assert len(records["Reservations"]) == 9
    assert len(records["GroupReservations"]) == 3
    assert len(records["GuestSessions"]) == 5
    assert len(records["Orders"]) == 9
    assert len(records["Feedback"]) == 6
    assert len(records["Notifications"]) == 9
    assert len(records["Reports"]) == 6
    assert len(records["ReportsHistory"]) == 3
    assert not records["ProfileImages"]
    assert caesar["price"] * 2 == Decimal("360")
    assert tom_yum["price"] == Decimal("220")
    assert caesar["price"] * 2 + tom_yum["price"] == Decimal("580")
    return records


def print_plan(records: dict[str, list[dict[str, Any]]]) -> None:
    print("DynamoDB demo seed plan (no AWS calls in preview mode):")
    for name in EXPECTED_TABLES:
        items = records[name]
        note = " (intentionally empty: no real S3 profile image assets are supplied)" if name == "ProfileImages" else ""
        print(f"  {name}: {len(items)} item(s){note}")
    print("  Level 1 group: Andrii=360, Olena=220, group total=580 (Owner session has no items).")
    print("  Restaurant targets: Juniper & Stone (Portland), Harbor & Hearth (Seattle), Cedar & Saffron (Austin).")
    print("  Existing records with matching primary keys will be skipped; other user data is not changed.")


def apply_seed(records: dict[str, list[dict[str, Any]]], profile: str, region: str, expected_account: str) -> None:
    session = boto3.Session(profile_name=profile, region_name=region)
    sts = session.client("sts")
    account = sts.get_caller_identity()["Account"]
    if account != expected_account:
        raise RuntimeError(f"Refusing to write: AWS account is {account}, expected {expected_account}.")

    dynamodb = session.resource("dynamodb")
    client = session.client("dynamodb")
    for table_name in EXPECTED_TABLES.values():
        client.describe_table(TableName=table_name)

    inserted = 0
    skipped = 0
    for table_name, items in records.items():
        if not items:
            continue
        description = client.describe_table(TableName=table_name)["Table"]["KeySchema"]
        key_names = [entry["AttributeName"] for entry in description]
        names = {f"#k{index}": key for index, key in enumerate(key_names)}
        condition = " AND ".join(f"attribute_not_exists(#k{index})" for index in range(len(key_names)))
        table = dynamodb.Table(table_name)
        for item in items:
            try:
                table.put_item(
                    Item=item,
                    ConditionExpression=condition,
                    ExpressionAttributeNames=names,
                )
                inserted += 1
            except ClientError as error:
                if error.response.get("Error", {}).get("Code") == "ConditionalCheckFailedException":
                    skipped += 1
                    continue
                raise
    print(f"Seed complete in account {account}, region {region}: {inserted} inserted, {skipped} existing records skipped.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", default="restaurant2")
    parser.add_argument("--region", default="us-east-2")
    parser.add_argument("--expected-account", default="135483674259")
    identity_group = parser.add_mutually_exclusive_group()
    identity_group.add_argument("--cognito-ids-file", type=Path, help="JSON output from setup_demo_cognito.py.")
    identity_group.add_argument("--owner-user-id", help="Existing Cognito sub for the demo customer/Owner.")
    parser.add_argument(
        "--waiter-user-ids",
        nargs=6,
        metavar="COGNITO_SUB",
        help="Six existing Cognito waiter subs, ordered as two IDs per restaurant.",
    )
    parser.add_argument("--apply", action="store_true", help="Write missing records after account/table checks.")
    args = parser.parse_args()

    try:
        if args.cognito_ids_file:
            identity_data = json.loads(args.cognito_ids_file.read_text(encoding="utf-8"))
            owner_user_id = identity_data["ownerUserId"]
            waiter_user_ids = identity_data["waiterUserIds"]
            if not isinstance(owner_user_id, str) or not isinstance(waiter_user_ids, list):
                raise ValueError("Cognito ID file must contain ownerUserId and a waiterUserIds array.")
        else:
            if not args.owner_user_id or not args.waiter_user_ids:
                parser.error("Provide --cognito-ids-file or both --owner-user-id and --waiter-user-ids.")
            owner_user_id = args.owner_user_id
            waiter_user_ids = args.waiter_user_ids
        records = build_seed(owner_user_id, waiter_user_ids)
    except (ValueError, KeyError, json.JSONDecodeError) as error:
        parser.error(f"Invalid Cognito IDs: {error}")

    print_plan(records)
    if args.apply:
        apply_seed(records, args.profile, args.region, args.expected_account)
    else:
        print("Preview only. No AWS credentials were used and no data was written.")
    return 0


if __name__ == "__main__":
    sys.exit(main())



