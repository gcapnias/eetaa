# EETAA Settlement Administrative Changes

Domain context for ingesting and tracking Greek settlement administrative history published by EETAA (Hellenic Agency for Local Development and Local Government).

## Language

**Settlement**:
An officially recognized Greek populated locality or community identified by a unique EETAA numerical identifier (`id` or `sid`).
_Avoid_: Village, town, city, locality, place

**Administrative Change**:
An official statutory transition modifying a settlement's legal status, name, or local government attachment (such as annexation, renaming, merger, or transfer to another municipality/prefecture), enacted via a Government Gazette issue.
_Avoid_: Mutation, transition, modification, update

**Government Gazette (FEK)**:
The official statutory journal of the Hellenic Republic (Φύλλο Εφημερίδας της Κυβερνήσεως) that legalizes an administrative change, specified by year, issue number, and publication date.
_Avoid_: Decree, legislation, bulletin, gazette issue

**Prefecture (Nomos)**:
The regional administrative unit of Greece to which a settlement was historically assigned at the time of a specific administrative change.
_Avoid_: County, province, region, department

**Settlement Queue**:
The persistent local inventory that tracks each known settlement identifier, its crawl lifecycle state, attempt count, and last fetch timestamp.
_Avoid_: Task list, fetch job, work items, download queue
