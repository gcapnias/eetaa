# EETAA Administrative Changes

Domain context for ingesting and tracking Greek settlement and local authority administrative history published by EETAA (Hellenic Agency for Local Development and Local Government).

## Language

**Settlement**:
An officially recognized Greek populated locality identified by a unique EETAA numerical identifier (`id` or `sid`).
_Avoid_: Village, town, city, locality, place

**Local Authority**:
An officially recognized Greek first-level local government organization (Municipality / Δήμος or Community / Κοινότητα) identified by a unique EETAA numerical identifier (`id` or `sid`).
_Avoid_: OTA, district, council, township, commune

**Authority Type**:
The statutory classification of a local authority as either a Municipality (Δήμος, coded 'Δ') or Community (Κοινότητα, coded 'Κ').
_Avoid_: OTA type, entity category, administration tier, organization kind

**Administrative Change**:
An official statutory transition modifying an entity's legal status, name, or administrative attachment (such as annexation, renaming, merger, or transfer to another municipality/prefecture), enacted via a Government Gazette issue.
_Avoid_: Mutation, transition, modification, update

**Government Gazette (FEK)**:
The official statutory journal of the Hellenic Republic (Φύλλο Εφημερίδας της Κυβερνήσεως) that legalizes an administrative change, specified by year, issue number, and publication date.
_Avoid_: Decree, legislation, bulletin, gazette issue

**Prefecture (Nomos)**:
The regional administrative unit of Greece to which a settlement or local authority was historically assigned at the time of a specific administrative change.
_Avoid_: County, province, region, department

**Settlement Queue**:
The persistent local inventory that tracks each known settlement identifier, its crawl lifecycle state, attempt count, and last fetch timestamp.
_Avoid_: Task list, fetch job, work items, download queue

**Local Authority Queue**:
The persistent local inventory that tracks each known local authority identifier, its crawl lifecycle state, attempt count, and last fetch timestamp.
_Avoid_: OTA queue, municipality queue, job list
