

-- 3. Создаем домены
CREATE DOMAIN account_type AS VARCHAR(20) CHECK (VALUE IN ('cash','card','bank','savings'));
CREATE DOMAIN op_type AS VARCHAR(10) CHECK (VALUE IN ('income','expense'));
CREATE DOMAIN positive_money AS NUMERIC(14,2) CHECK (VALUE > 0);


CREATE TABLE users (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(255) NOT NULL UNIQUE,
    created_at TIMESTAMP DEFAULT NOW()
);

-- 5. Создаем таблицу accounts
CREATE TABLE accounts (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name VARCHAR(100) NOT NULL,
    type account_type NOT NULL,
    balance positive_money NOT NULL DEFAULT 0,
    currency CHAR(3) NOT NULL DEFAULT 'RUB'
);

-- 6. Создаем таблицу categories
CREATE TABLE categories (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name VARCHAR(100) NOT NULL,
    type op_type NOT NULL,
    parent_id INT REFERENCES categories(id) ON DELETE SET NULL
);

-- 7. Создаем таблицу transactions
CREATE TABLE transactions (
    id BIGSERIAL PRIMARY KEY,
    account_id INT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    category_id INT REFERENCES categories(id) ON DELETE SET NULL,
    amount positive_money NOT NULL,
    type op_type NOT NULL,
    date DATE NOT NULL,
    created_at TIMESTAMP DEFAULT NOW()
);

-- 8. Создаем таблицу budgets
CREATE TABLE budgets (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    category_id INT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
    period_start DATE NOT NULL,
    period_end DATE NOT NULL,
    amount_limit positive_money NOT NULL,
    UNIQUE (user_id, category_id, period_start)
);
