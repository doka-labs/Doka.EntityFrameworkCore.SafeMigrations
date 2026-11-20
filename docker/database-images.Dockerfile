# WHY: Tests, benchmarks and native qualification read these FROM references as
# data. Keep image identity in one place without changing native server defaults.
# MySQL, MariaDB and PostgreSQL use multi-platform index digests; SQL Server
# remains qualified only on supported native x64 hosts.

FROM mysql:8.4.13@sha256:a81d043930e51745522bd5c430961cf8286f0a23f0a9a2a6e0108f7ea71b0902 AS mysql84
FROM mysql:9.7.4@sha256:df90258da46f6e7ff8574ff84f17618aba0765cac749928299c69fd4e7cb8ad8 AS mysql97
FROM mariadb:10.11.19@sha256:7d829180b5b2a9ba74f6242876ab401a0d7dd338a807e522ef92bc9698760f20 AS mariadb1011
FROM mariadb:11.4.13@sha256:5e73baf8179b29f115101bb102763a47a017361f3e9792daccba6646c3ab1532 AS mariadb114
FROM mariadb:11.8.9@sha256:d36f18d65a0459510b8b0f643b226a6d299e3206486e2b64ff20754a6d291707 AS mariadb118
FROM mariadb:12.3.3@sha256:4ebbcf100a3ba320766dfa0e1af008df12261301db6ef72798c7650f70794aed AS mariadb123
FROM postgres:14.25@sha256:c0e9c0e9bac7e4c1834b698217a03ee5c708de30d313c84a506972de0e24b059 AS postgres14
FROM postgres:15.20@sha256:10f9e886f772e9d6484350b043bcf990ead67ae5d9988ddce265d72a299055d5 AS postgres15
FROM postgres:16.16@sha256:3950ad7c3148ca2175ef1b6c36c9efbf78957ae8fbe8b3a5d7c35af50500382f AS postgres16
FROM postgres:17.12@sha256:b60e90042aa6b8dbecd58e224b3e36114dbf8ad754465264c2476ad58a86e45c AS postgres17
FROM postgres:18.7@sha256:e754b839f447639b7004895f4d8a4bfcb4e9dbe3a1bc035f27f646562a16fd61 AS postgres18
FROM mcr.microsoft.com/mssql/server:2019-latest@sha256:debdc21628cddd1a4433371c601c8dee9bad3533ae8b18fe9dc8c5596368b10d AS sqlserver2019
FROM mcr.microsoft.com/mssql/server:2022-latest@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f AS sqlserver2022
FROM mcr.microsoft.com/mssql/server:2025-latest@sha256:05644dad1266ca3d445d1e8f6d2ebb9a6a03df9a39376e0f7b48917356dca9d9 AS sqlserver2025
