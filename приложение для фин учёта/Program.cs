
using Microsoft.Data.Sqlite;
using System.Data;
using System.Drawing;

namespace FinUchet;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Database.Initialize();
        Application.Run(new MainForm());
    }
}

static class Database
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FinUchet", "finance.db");
    public static string ConnectionString => $"Data Source={FilePath}";

    public static SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public static void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS users(
          id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL,
          email TEXT NOT NULL UNIQUE, created_at TEXT DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE IF NOT EXISTS accounts(
          id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL,
          name TEXT NOT NULL, type TEXT NOT NULL CHECK(type IN ('cash','card','bank','savings')),
          balance REAL NOT NULL DEFAULT 0 CHECK(balance >= 0),
          opening_balance REAL NOT NULL DEFAULT 0 CHECK(opening_balance >= 0),
          currency TEXT NOT NULL DEFAULT 'RUB',
          FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS categories(
          id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL,
          name TEXT NOT NULL, type TEXT NOT NULL CHECK(type IN ('income','expense')),
          parent_id INTEGER NULL, FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
          FOREIGN KEY(parent_id) REFERENCES categories(id) ON DELETE SET NULL);
        CREATE TABLE IF NOT EXISTS transactions(
          id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL,
          category_id INTEGER NULL, amount REAL NOT NULL CHECK(amount > 0),
          type TEXT NOT NULL CHECK(type IN ('income','expense')), date TEXT NOT NULL,
          note TEXT DEFAULT '',
          FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE,
          FOREIGN KEY(category_id) REFERENCES categories(id) ON DELETE SET NULL);
        CREATE TABLE IF NOT EXISTS budgets(
          id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL,
          category_id INTEGER NOT NULL, period_start TEXT NOT NULL,
          period_end TEXT NOT NULL, amount_limit REAL NOT NULL CHECK(amount_limit > 0),
          UNIQUE(user_id, category_id, period_start),
          FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
          FOREIGN KEY(category_id) REFERENCES categories(id) ON DELETE CASCADE);
        """;
        cmd.ExecuteNonQuery();
        // Миграция для базы, созданной предыдущей версией приложения.
        using (var migration = c.CreateCommand())
        {
            migration.CommandText = "PRAGMA table_info(accounts)";
            using var reader = migration.ExecuteReader();
            bool hasOpening = false;
            while (reader.Read()) if (reader.GetString(1) == "opening_balance") hasOpening = true;
            reader.Close();
            if (!hasOpening)
            {
                using var alter = c.CreateCommand();
                alter.CommandText = "ALTER TABLE accounts ADD COLUMN opening_balance REAL NOT NULL DEFAULT 100000";
                alter.ExecuteNonQuery();
                using var sync = c.CreateCommand();
                sync.CommandText = "UPDATE accounts SET opening_balance=balance";
                sync.ExecuteNonQuery();
            }
        }

        using var count = c.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM users";
        if (Convert.ToInt32(count.ExecuteScalar()) == 0)
        {
            using var tx = c.BeginTransaction();
            Exec(c, tx, "INSERT INTO users(name,email) VALUES('Мой профиль','local@finuchet.local')");
            Exec(c, tx, "INSERT INTO accounts(user_id,name,type,balance,opening_balance,currency) VALUES(1,'Основная карта','card',100000,100000,'RUB')");
            string[] inc = { "Зарплата", "Подработка", "Подарки", "Прочие доходы" };
            string[] exp = { "Продукты", "Транспорт", "Жильё", "Развлечения", "Здоровье", "Покупки", "Связь", "Прочие расходы" };
            foreach (var x in inc) InsertCategory(c, tx, x, "income");
            foreach (var x in exp) InsertCategory(c, tx, x, "expense");
            tx.Commit();
        }
    }

    static void Exec(SqliteConnection c, SqliteTransaction tx, string sql)
    { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    static void InsertCategory(SqliteConnection c, SqliteTransaction tx, string name, string type)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO categories(user_id,name,type) VALUES(1,$n,$t)";
        cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$t", type); cmd.ExecuteNonQuery();
    }

    public static DataTable Query(string sql, params (string, object?)[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (k,v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var dt = new DataTable(); dt.Load(r); return dt;
    }

    public static int Execute(string sql, params (string, object?)[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (k,v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static object Scalar(string sql, params (string, object?)[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (k,v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteScalar() ?? 0;
    }

    public static void RecalculateBalances()
    {
        Execute("""
        UPDATE accounts SET balance = opening_balance +
          COALESCE((SELECT SUM(CASE WHEN type='income' THEN amount ELSE -amount END)
          FROM transactions WHERE account_id=accounts.id),0)
        """);
    }
}

public class MainForm : Form
{
    readonly TabControl tabs = new();
    readonly DataGridView historyGrid = Grid();
    readonly DataGridView accountsGrid = Grid();
    readonly DataGridView categoriesGrid = Grid();
    readonly DataGridView budgetsGrid = Grid();
    readonly DataGridView analyticsGrid = Grid();
    readonly Label totalLabel = new();
    readonly Label incomeLabel = new();
    readonly Label expenseLabel = new();
    readonly Label netLabel = new();
    readonly string[] tabNames = { "Обзор", "Операции", "Счета", "Категории", "Бюджеты", "Аналитика" };

    public MainForm()
    {
        Text = "ФинУчёт — личные финансы";
        MinimumSize = new Size(1050, 700);
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(245, 247, 250);

        var header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Color.FromArgb(25, 39, 61) };
        var brand = new Label { Text = "ФИНУЧЁТ", ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true, Location = new Point(24, 17) };
        var subtitle = new Label { Text = "Личные финансы под контролем", ForeColor = Color.LightGray, AutoSize = true, Location = new Point(160, 25) };
        header.Controls.Add(brand); header.Controls.Add(subtitle);
        Controls.Add(tabs); Controls.Add(header);
        tabs.Dock = DockStyle.Fill;
        BuildTabs();
        RefreshAll();
    }

    static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        RowHeadersVisible = false
    };

    TabPage AddTab(string title)
    {
        var p = new TabPage(title) { BackColor = Color.FromArgb(245,247,250), Padding = new Padding(16) };
        tabs.TabPages.Add(p); return p;
    }

    void BuildTabs()
    {
        BuildOverview(AddTab("Обзор"));
        BuildHistory(AddTab("Операции"));
        BuildAccounts(AddTab("Счета"));
        BuildCategories(AddTab("Категории"));
        BuildBudgets(AddTab("Бюджеты"));
        BuildAnalytics(AddTab("Аналитика"));
    }

    Button Btn(string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(12,7,12,7),
            BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4) };
        b.FlatAppearance.BorderSize = 0; b.Click += (_,_) => { try { action(); RefreshAll(); } catch(Exception ex) { MessageBox.Show(ex.Message,"Ошибка",MessageBoxButtons.OK,MessageBoxIcon.Error); } };
        return b;
    }

    void BuildOverview(TabPage p)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(0,10,0,10) };
        foreach (var l in new[] { totalLabel, incomeLabel, expenseLabel, netLabel })
        {
            l.AutoSize = false; l.Size = new Size(245, 105); l.Padding = new Padding(16);
            l.BackColor = Color.White; l.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            l.TextAlign = ContentAlignment.MiddleLeft; flow.Controls.Add(l);
        }
        var info = new Label { Dock = DockStyle.Fill, Text = "Добро пожаловать в ФинУчёт!\n\nДобавляйте доходы и расходы, распределяйте их по категориям, задавайте месячные лимиты и анализируйте движение денег.\n\nНачальный остаток основного счёта — 100 000 ₽. Балансы пересчитываются автоматически по операциям.", Font = new Font("Segoe UI", 12), Padding = new Padding(18), BackColor = Color.White };
        p.Controls.Add(info); p.Controls.Add(flow);
    }

    void BuildHistory(TabPage p)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52 };
        bar.Controls.Add(Btn("+ Добавить операцию", AddTransaction));
        bar.Controls.Add(Btn("Удалить выбранную", DeleteTransaction));
        bar.Controls.Add(Btn("Обновить", RefreshAll));
        p.Controls.Add(historyGrid); p.Controls.Add(bar);
    }

    void BuildAccounts(TabPage p)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52 };
        bar.Controls.Add(Btn("+ Добавить счёт", AddAccount));
        bar.Controls.Add(Btn("Удалить выбранный", DeleteAccount));
        p.Controls.Add(accountsGrid); p.Controls.Add(bar);
    }

    void BuildCategories(TabPage p)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52 };
        bar.Controls.Add(Btn("+ Добавить категорию", AddCategory));
        bar.Controls.Add(Btn("Удалить выбранную", DeleteCategory));
        p.Controls.Add(categoriesGrid); p.Controls.Add(bar);
    }

    void BuildBudgets(TabPage p)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52 };
        bar.Controls.Add(Btn("+ Установить бюджет", AddBudget));
        bar.Controls.Add(Btn("Удалить выбранный", DeleteBudget));
        p.Controls.Add(budgetsGrid); p.Controls.Add(bar);
    }

    void BuildAnalytics(TabPage p)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52 };
        bar.Controls.Add(Btn("Расходы за месяц", () => LoadAnalytics("expenses")));
        bar.Controls.Add(Btn("Контроль бюджетов", () => LoadAnalytics("budgets")));
        bar.Controls.Add(Btn("Топ-5 покупок", () => LoadAnalytics("top")));
        bar.Controls.Add(Btn("Движение по месяцам", () => LoadAnalytics("cash")));
        bar.Controls.Add(Btn("Проверка балансов", () => LoadAnalytics("negative")));
        p.Controls.Add(analyticsGrid); p.Controls.Add(bar);
    }

    void RefreshAll()
    {
        Database.RecalculateBalances();
        historyGrid.DataSource = Database.Query("""
          SELECT t.id AS '№', t.date AS 'Дата',
          CASE t.type WHEN 'income' THEN 'Доход' ELSE 'Расход' END AS 'Тип',
          COALESCE(c.name,'Без категории') AS 'Категория', a.name AS 'Счёт',
          t.amount AS 'Сумма, ₽', t.note AS 'Комментарий'
          FROM transactions t JOIN accounts a ON a.id=t.account_id
          LEFT JOIN categories c ON c.id=t.category_id ORDER BY t.date DESC,t.id DESC
        """);
        accountsGrid.DataSource = Database.Query("SELECT id AS '№',name AS 'Название',CASE type WHEN 'cash' THEN 'Наличные' WHEN 'card' THEN 'Карта' WHEN 'bank' THEN 'Банк' ELSE 'Накопления' END AS 'Тип',balance AS 'Баланс, ₽',currency AS 'Валюта' FROM accounts ORDER BY id");
        categoriesGrid.DataSource = Database.Query("SELECT id AS '№',name AS 'Название',CASE type WHEN 'income' THEN 'Доход' ELSE 'Расход' END AS 'Тип' FROM categories ORDER BY type,name");
        budgetsGrid.DataSource = Database.Query("""
          SELECT b.id AS '№',c.name AS 'Категория',b.period_start AS 'С',b.period_end AS 'По',
          b.amount_limit AS 'Лимит, ₽',
          COALESCE((SELECT SUM(t.amount) FROM transactions t WHERE t.category_id=c.id AND t.type='expense' AND t.date BETWEEN b.period_start AND b.period_end),0) AS 'Потрачено, ₽',
          b.amount_limit-COALESCE((SELECT SUM(t.amount) FROM transactions t WHERE t.category_id=c.id AND t.type='expense' AND t.date BETWEEN b.period_start AND b.period_end),0) AS 'Остаток, ₽'
          FROM budgets b JOIN categories c ON c.id=b.category_id ORDER BY b.period_start DESC
        """);
        decimal total = Convert.ToDecimal(Database.Scalar("SELECT COALESCE(SUM(balance),0) FROM accounts"));
        var start = DateTime.Today.ToString("yyyy-MM-01");
        var end = DateTime.Today.ToString("yyyy-MM-dd");
        decimal inc = Convert.ToDecimal(Database.Scalar("SELECT COALESCE(SUM(amount),0) FROM transactions WHERE type='income' AND date BETWEEN $s AND $e",("$s",start),("$e",end)));
        decimal exp = Convert.ToDecimal(Database.Scalar("SELECT COALESCE(SUM(amount),0) FROM transactions WHERE type='expense' AND date BETWEEN $s AND $e",("$s",start),("$e",end)));
        totalLabel.Text = $"ОБЩИЙ БАЛАНС\n{total:N2} ₽";
        incomeLabel.Text = $"ДОХОДЫ ЗА МЕСЯЦ\n{inc:N2} ₽";
        expenseLabel.Text = $"РАСХОДЫ ЗА МЕСЯЦ\n{exp:N2} ₽";
        netLabel.Text = $"РАЗНИЦА\n{(inc-exp):N2} ₽";
    }

    void AddTransaction()
    {
        var accounts = Database.Query("SELECT id,name FROM accounts");
        var categories = Database.Query("SELECT id,name,type FROM categories");
        if (accounts.Rows.Count == 0) throw new Exception("Сначала создайте счёт.");
        using var f = new Form { Text="Новая операция", Size=new Size(430,470), StartPosition=FormStartPosition.CenterParent, FormBorderStyle=FormBorderStyle.FixedDialog, MaximizeBox=false, MinimizeBox=false };
        var type = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Left=170,Top=20,Width=210 };
        type.Items.AddRange(new object[]{"Расход","Доход"}); type.SelectedIndex=0;
        var amount = new NumericUpDown { Left=170,Top=65,Width=210,Maximum=100000000,DecimalPlaces=2,Minimum=0.01M,ThousandsSeparator=true };
        var date = new DateTimePicker { Left=170,Top=110,Width=210,Format=DateTimePickerFormat.Short };
        var account = new ComboBox { Left=170,Top=155,Width=210,DropDownStyle=ComboBoxStyle.DropDownList,DataSource=accounts,DisplayMember="name",ValueMember="id" };
        var cat = new ComboBox { Left=170,Top=200,Width=210,DropDownStyle=ComboBoxStyle.DropDownList,DataSource=categories.Copy(),DisplayMember="name",ValueMember="id" };
        var note = new TextBox { Left=170,Top=245,Width=210 };
        AddField(f,"Тип операции",type,20); AddField(f,"Сумма, ₽",amount,65); AddField(f,"Дата",date,110); AddField(f,"Счёт",account,155); AddField(f,"Категория",cat,200); AddField(f,"Комментарий",note,245);
        type.SelectedIndexChanged += (_,_) => { var t=type.SelectedIndex==0?"expense":"income"; var dt=categories.Select($"type='{t}'"); cat.DataSource=dt.Length>0?dt.CopyToDataTable():categories.Clone(); };
        type.SelectedIndex=0;
        var save = new Button { Text="Сохранить",Left=170,Top=310,Width=120,DialogResult=DialogResult.OK };
        f.Controls.Add(save); f.AcceptButton=save;
        if(f.ShowDialog(this)!=DialogResult.OK) return;
        var op = type.SelectedIndex==0?"expense":"income";
        var amt = (double)amount.Value;
        if(op=="expense")
        {
            var bal=Convert.ToDouble(Database.Scalar("SELECT balance FROM accounts WHERE id=$id",("$id",account.SelectedValue)));
            if(amt>bal) throw new Exception($"Недостаточно средств на счёте. Доступно: {bal:N2} ₽");
        }
        Database.Execute("INSERT INTO transactions(account_id,category_id,amount,type,date,note) VALUES($a,$c,$m,$t,$d,$n)",
            ("$a",account.SelectedValue),("$c",cat.SelectedValue),("$m",amt),("$t",op),("$d",date.Value.ToString("yyyy-MM-dd")),("$n",note.Text));
    }

    static void AddField(Form f,string label,Control c,int y)
    {
        f.Controls.Add(new Label{Text=label,Left=20,Top=y+4,Width=140});
        c.Left=170;c.Top=y;c.Width=210;f.Controls.Add(c);
    }

    int? SelectedId(DataGridView g)
    {
        if(g.CurrentRow==null || g.CurrentRow.Cells[0].Value==null) return null;
        return Convert.ToInt32(g.CurrentRow.Cells[0].Value);
    }
    void DeleteTransaction(){var id=SelectedId(historyGrid);if(id==null)return;if(MessageBox.Show("Удалить операцию?","Подтверждение",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Database.Execute("DELETE FROM transactions WHERE id=$id",("$id",id));}
    void AddAccount()
    {
        string name=Ask("Название счёта:","Новый счёт");if(string.IsNullOrWhiteSpace(name))return;
        string kind=Choose("Тип счёта",new[]{"Наличные","Карта","Банк","Накопления"});if(kind=="")return;
        decimal initial=AskNumber("Начальный баланс (₽):",0);
        string k=kind switch{"Наличные"=>"cash","Карта"=>"card","Банк"=>"bank",_=>"savings"};
        Database.Execute("INSERT INTO accounts(user_id,name,type,balance,opening_balance,currency) VALUES(1,$n,$t,$b,$b,'RUB')",("$n",name),("$t",k),("$b",(double)initial));
    }
    void DeleteAccount(){var id=SelectedId(accountsGrid);if(id==null)return;if(MessageBox.Show("Удаление счёта удалит и связанные операции. Продолжить?","Подтверждение",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Database.Execute("DELETE FROM accounts WHERE id=$id",("$id",id));}
    void AddCategory()
    {
        string name=Ask("Название категории:","Новая категория");if(string.IsNullOrWhiteSpace(name))return;
        string type=Choose("Тип категории",new[]{"Расход","Доход"});if(type=="")return;
        Database.Execute("INSERT INTO categories(user_id,name,type) VALUES(1,$n,$t)",("$n",name),("$t",type=="Доход"?"income":"expense"));
    }
    void DeleteCategory(){var id=SelectedId(categoriesGrid);if(id==null)return;if(MessageBox.Show("Удалить категорию? Операции сохранятся без категории.","Подтверждение",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Database.Execute("DELETE FROM categories WHERE id=$id",("$id",id));}
    void AddBudget()
    {
        var cats=Database.Query("SELECT id,name FROM categories WHERE type='expense'");
        if(cats.Rows.Count==0)throw new Exception("Нет категорий расходов.");
        string name=Choose("Категория бюджета",cats.AsEnumerable().Select(r=>$"{r["id"]}|{r["name"]}").ToArray());
        if(name=="")return;int id=int.Parse(name.Split('|')[0]);
        decimal limit=AskNumber("Лимит на текущий месяц (₽):",10000);if(limit<=0)return;
        var start=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1);var end=start.AddMonths(1).AddDays(-1);
        Database.Execute("INSERT OR REPLACE INTO budgets(user_id,category_id,period_start,period_end,amount_limit) VALUES(1,$c,$s,$e,$l)",("$c",id),("$s",start.ToString("yyyy-MM-dd")),("$e",end.ToString("yyyy-MM-dd")),("$l",(double)limit));
    }
    void DeleteBudget(){var id=SelectedId(budgetsGrid);if(id==null)return;Database.Execute("DELETE FROM budgets WHERE id=$id",("$id",id));}

    void LoadAnalytics(string mode)
    {
        string sql=mode switch
        {
            "expenses"=>"""
              SELECT c.name AS 'Категория',SUM(t.amount) AS 'Потрачено, ₽',
              ROUND(SUM(t.amount)*100.0/(SELECT SUM(amount) FROM transactions WHERE type='expense' AND date>=date('now','start of month')),2) AS 'Доля, %'
              FROM transactions t JOIN categories c ON c.id=t.category_id
              WHERE t.type='expense' AND t.date>=date('now','start of month')
              GROUP BY c.name ORDER BY SUM(t.amount) DESC
            """,
            "budgets"=>"""
              SELECT c.name AS 'Категория',b.amount_limit AS 'Лимит, ₽',
              COALESCE(SUM(t.amount),0) AS 'Потрачено, ₽',
              b.amount_limit-COALESCE(SUM(t.amount),0) AS 'Остаток, ₽',
              CASE WHEN COALESCE(SUM(t.amount),0)>b.amount_limit THEN 'Превышен'
                   WHEN COALESCE(SUM(t.amount),0)>b.amount_limit*0.8 THEN 'Почти исчерпан' ELSE 'В норме' END AS 'Статус'
              FROM budgets b JOIN categories c ON c.id=b.category_id
              LEFT JOIN transactions t ON t.category_id=c.id AND t.type='expense' AND t.date BETWEEN b.period_start AND b.period_end
              GROUP BY b.id,c.name,b.amount_limit
            """,
            "top"=>"""
              SELECT t.date AS 'Дата',c.name AS 'Категория',t.amount AS 'Сумма, ₽',a.name AS 'Счёт'
              FROM transactions t JOIN accounts a ON a.id=t.account_id LEFT JOIN categories c ON c.id=t.category_id
              WHERE t.type='expense' ORDER BY t.amount DESC LIMIT 5
            """,
            "cash"=>"""
              SELECT substr(t.date,1,7) AS 'Месяц',
              SUM(CASE WHEN t.type='income' THEN t.amount ELSE 0 END) AS 'Доходы, ₽',
              SUM(CASE WHEN t.type='expense' THEN t.amount ELSE 0 END) AS 'Расходы, ₽',
              SUM(CASE WHEN t.type='income' THEN t.amount ELSE -t.amount END) AS 'Чистый поток, ₽'
              FROM transactions t GROUP BY substr(t.date,1,7) ORDER BY substr(t.date,1,7) DESC
            """,
            _=>"SELECT id AS 'Счёт №',name AS 'Счёт',balance AS 'Баланс, ₽' FROM accounts WHERE balance<0"
        };
        analyticsGrid.DataSource=Database.Query(sql);
    }

    static string Ask(string prompt,string title)
    {
        using var f=new Form{Text=title,Width=380,Height=160,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog};
        var l=new Label{Text=prompt,Left=15,Top=15,Width=330};var t=new TextBox{Left=15,Top=45,Width=330};
        var b=new Button{Text="ОК",Left=260,Top=80,DialogResult=DialogResult.OK};f.Controls.AddRange(new Control[]{l,t,b});f.AcceptButton=b;
        return f.ShowDialog()==DialogResult.OK?t.Text:"";
    }
    static decimal AskNumber(string prompt,decimal initial)
    {
        using var f=new Form{Text=prompt,Width=360,Height=160,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog};
        var n=new NumericUpDown{Left=15,Top=20,Width=310,Maximum=100000000,DecimalPlaces=2,Value=initial};
        var b=new Button{Text="ОК",Left=245,Top=65,DialogResult=DialogResult.OK};f.Controls.AddRange(new Control[]{n,b});f.AcceptButton=b;
        return f.ShowDialog()==DialogResult.OK?n.Value:0;
    }
    static string Choose(string title,string[] options)
    {
        using var f=new Form{Text=title,Width=380,Height=160,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog};
        var c=new ComboBox{Left=15,Top=20,Width=330,DropDownStyle=ComboBoxStyle.DropDownList};c.Items.AddRange(options);if(c.Items.Count>0)c.SelectedIndex=0;
        var b=new Button{Text="ОК",Left=260,Top=65,DialogResult=DialogResult.OK};f.Controls.AddRange(new Control[]{c,b});f.AcceptButton=b;
        if(f.ShowDialog()!=DialogResult.OK||c.SelectedItem==null)return "";return c.SelectedItem.ToString()!;
    }
}
