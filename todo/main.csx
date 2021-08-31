#load ".meta/globals.csx" // This is required for Intellisense in VS Code, etc. DO NOT TOUCH THIS LINE!
/*    
Description: A simple to-do skill for your team

Package URL: https://ab.bot/packages/aseriousbiz/todo

Usage:
`@abbot todo list` -- lists your todo items
`@abbot todo <description>` -- add a todo item
`@abbot todo complete <number>` -- complete the item numbered <number>
`@abbot todo assign <number> to <person>` -- assigns item <number> to <person>
*/
class Todo {
    public int Id { get; set; }
    public string Description { get; set; }
    public string AssignedTo { get; set; }
    public DateTime AssignedAt { get; set; }
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CompletedBy {get; set; }
	public DateTime? CompletedAt { get; set; }	
}

var (cmd, idArg, _, assigneeArg) = Bot.Arguments;
var command = cmd is not IMissingArgument ? cmd.Value.ToUpperInvariant() : "LIST";

// This example uses one DB for everything, but you could create customized lists
// by changing the name here; for example you could set the dbname to match the room
// the script was called from.
string dbname = $"my-group-todo-list";
var db = await Bot.Brain.GetAsAsync<List<Todo>>(dbname);
if (db is null) {
    db = new List<Todo>();
    await Bot.ReplyAsync("no db for user found, creating one...");
    await SaveDb();
}

Task action = command switch {
    "LIST" => ListOpenTodos(),
    "COMPLETE" => CompleteTodo(idArg),
    "COMPLETED" => ListCompletedTodos(),
    "ASSIGN" => AssignTodo(idArg, assigneeArg),
    "DELETE" => DeleteTodo(idArg),
    _ => AddTodo(Bot.From.Id, Bot.Arguments.Value)
};
await action;

public async Task ListOpenTodos() {
    if (db.Count > 0) {
        var response = $"Here are all the open to-dos:";
        foreach (var item in db) {
            if (item.CompletedAt is null) {
                response += $"\n* #{item.Id}: {item.Description} (assigned to <@{item.AssignedTo}>)";
            }
        }
        await Bot.ReplyAsync(response);
    } else {
        await Bot.ReplyAsync("There aren't any open items to do. Try `@abbot todo help` to find out how to use this skill.");
    }
}

public async Task ListCompletedTodos() {
    if (db.Exists(t => t.CompletedAt.HasValue)) {
        var response = "Completed to-dos:";
        foreach (var item in db) {
            if (item.CompletedAt.HasValue) {
                response += $"\n * #{item.Id}: {item.Description} (completed by <@{item.CompletedBy}> at {item.CompletedAt}).";
            }
        }
        await Bot.ReplyAsync(response);
    } else {
        await Bot.ReplyAsync("Nobody has completed any items yet...");
    }
}

public async Task AddTodo(string owner, string description) {
    var todo = new Todo() {
        Id = db.Count + 1,
        Description = description,
        CreatedBy = owner,
        CreatedAt = DateTime.UtcNow,
        AssignedTo = owner,
        AssignedAt = DateTime.UtcNow,
        CompletedBy = string.Empty
    };
    
    db.Add(todo);
    await SaveDb();
    await Bot.ReplyAsync($"Added {todo.Id}: {description}");
}

public async Task CompleteTodo(IArgument idArg) {
    var id = idArg.ToInt32();
    if (id is null) {
        await Bot.ReplyAsync("COMPLETE expects a valid task number...");
        return;
    }
    
    var task = db.Find(t => t.Id == id.Value);

    if (task is not null) {
        task.CompletedAt = DateTime.Now;
        task.CompletedBy = Bot.From.Id;
        await SaveDb();
        await Bot.ReplyAsync($"Great, you completed item #{id}!");
    } else {
        await Bot.ReplyAsync($"Couldn't find a todo item #{id}");
    }
}

public async Task AssignTodo(IArgument idArg, IArgument assigneeArg) {
    var id = idArg.ToInt32();
    if (id is null) {
        await Bot.ReplyAsync("ASSIGN expects a valid task number (like `@abbot assign 3 to @gandalf`)");
        return;
    }
    if (assigneeArg is IMissingArgument) {
        await Bot.ReplyAsync($"You didn't say who to assign item ${id} to... (try something like `@abbot assign 3 to gandalf`)");
        return;
    }
    var task = db.Find(t => t.Id == id.Value);

    if (task is not null) {
        if (task.CompletedAt.HasValue) {
            await Bot.ReplyAsync($"Item #{id} has already been completed and can't be assigned.");
            return;
        }
        
        var assignee = assigneeArg switch {
            {Value: "me"} => Bot.From,
            IMentionArgument mentionArg => mentionArg.Mentioned,
            _ => null
        };
        if (assignee is null) {
            await Bot.ReplyAsync("I do not understand that assignee.");
            return;
        }
        
        task.AssignedAt = DateTime.Now;
        task.AssignedTo = assignee.Id;
        await SaveDb();
        await Bot.ReplyAsync($"Item #{id} is now assigned to {assignee}.");
    } else {
        await Bot.ReplyAsync($"Couldn't find a todo item #{id}.");
    }
}

public async Task DeleteTodo(IArgument idArg) {
    var id = idArg.ToInt32();
    if (id is null) {
        await Bot.ReplyAsync("DELETE expects a valid task number...");
        return;
    }
    var task = db.Find(t => t.Id == id.Value);
    db.Remove(task);
    await SaveDb();
    await Bot.ReplyAsync($"Deleted todo #{id}");
}

public async Task SaveDb() {
    await Bot.Brain.WriteAsync(dbname, db);
}